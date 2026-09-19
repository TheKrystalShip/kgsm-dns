using System.Net;
using System.Text;
using System.Text.Json.Serialization.Metadata;

using Microsoft.Extensions.Logging.Abstractions;

using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Identity;
using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Cluster.Messaging;
using TheKrystalShip.KGSM.Cluster.Storage;
using TheKrystalShip.KGSM.Dns.Member;
using TheKrystalShip.KGSM.Dns.Messages;
using TheKrystalShip.KGSM.Dns.Serving;

namespace TheKrystalShip.KGSM.Dns.Tests;

/// <summary>
/// A member tells the DNS anchor about itself again the moment the cluster had taken it for gone, and
/// asks for its names rather than depending on being reachable to be told them.
/// </summary>
/// <remarks>
/// A removed member's names are released once the removal has stood for a while, and a member that was
/// only slow to refute it has no address anyone can reach it at until it states itself again. Raising its
/// incarnation is how it refutes, so that is what brings the statement forward — and since the anchor's
/// answer travels to a name the member may not be serving, the member fetches it.
/// </remarks>
public sealed class RestatementTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"kgsm-dns-member-{Guid.NewGuid():N}");
    private readonly ClusterOptions _cluster;
    private readonly ClusterStateStore _state;
    private readonly MembersStore _members;
    private readonly SelfIncarnation _incarnation = new();
    private readonly CountingBus _bus = new();
    private readonly StubAnchor _anchor = new();
    private readonly DnsNameBook _book = new();
    private readonly DnsNamesFetcher _fetcher;
    private readonly DnsMemberOptions _options = new("home.ddns.net", TimeSpan.FromMinutes(15));

    public RestatementTests()
    {
        Directory.CreateDirectory(_directory);
        _cluster = new ClusterOptions
        {
            MemberId = "node-a",
            Secret = "test-secret-long-enough-to-sign-with-0123456789",
            StorePath = Path.Combine(_directory, "cluster.db"),
        }.Validate();
        var store = new ClusterStore(_cluster, NullLogger<ClusterStore>.Instance);
        _state = new ClusterStateStore(store, _cluster);
        _members = new MembersStore(store);

        var files = new CertificateFiles(Path.Combine(_directory, "tls"));
        var keeper = new CertificateKeeper(
            _book, files,
            new NginxSite(new NginxSiteOptions("kgsm-api", Path.Combine(_directory, "sites"), "/nonexistent.locations"),
                files, NullLogger<NginxSite>.Instance),
            new NoRequests(), NullLogger<CertificateKeeper>.Instance);
        _fetcher = new DnsNamesFetcher(
            new OneClient(_anchor), new ClusterTokenService(_cluster, NullLogger<ClusterTokenService>.Instance),
            _book, keeper, NullLogger<DnsNamesFetcher>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private async Task AnchorHoldsDnsAsync()
    {
        await _members.UpsertAsync(
            MemberRow.New("dns-anchor", MemberKind.Anchor) with { Url = "https://dns.anchors.example.com" }, default);
        Assert.True(await _state.TryClaimAsync(DnsCapability.Name, "dns-anchor", default));
    }

    private DnsSelfWorker SelfWorker() => new(
        _cluster, _state, _members, _incarnation, _bus, _book, _fetcher, _options, NullLogger<DnsSelfWorker>.Instance);

    [Fact]
    public async Task A_rising_incarnation_restates_where_the_member_is_reached()
    {
        await AnchorHoldsDnsAsync();
        DnsSelfWorker worker = SelfWorker();

        await worker.StateAsync(default);
        await worker.StateAsync(default);
        Assert.Equal(1, _bus.Sent(DnsMessageTypes.Self));

        _incarnation.RaiseToRefute(_incarnation.Current + 3);
        await worker.StateAsync(default);
        Assert.Equal(2, _bus.Sent(DnsMessageTypes.Self));
    }

    [Fact]
    public async Task A_rising_incarnation_restates_the_nodes_game_servers()
    {
        await AnchorHoldsDnsAsync();
        var worker = new DnsInstancesWorker(
            _cluster, _state, _members, _incarnation, _bus, new OneServer(), _options,
            NullLogger<DnsInstancesWorker>.Instance);

        await worker.StateAsync(default);
        await worker.StateAsync(default);
        Assert.Equal(1, _bus.Sent(DnsMessageTypes.Instances));

        _incarnation.RaiseToRefute(_incarnation.Current + 3);
        await worker.StateAsync(default);
        Assert.Equal(2, _bus.Sent(DnsMessageTypes.Instances));
    }

    [Fact]
    public async Task A_member_asks_for_its_names_and_keeps_the_answer()
    {
        await AnchorHoldsDnsAsync();
        _anchor.Answer = (HttpStatusCode.OK, """{"names":[{"name":"jessie.nodes.example.com","kind":"member","key":"","state":"pending","note":""}]}""");

        await SelfWorker().StateAsync(default);

        Assert.Equal(["https://dns.anchors.example.com" + DnsRoutes.Names], _anchor.Asked);
        Assert.True(_book.Answered);
        Assert.Equal(["jessie.nodes.example.com"], _book.Held);
        Assert.StartsWith("Bearer ", _anchor.Authorization);
    }

    [Fact]
    public async Task An_anchor_that_has_not_heard_the_member_yet_changes_nothing()
    {
        // Racing its own statement, the member is not yet known to the anchor. Reading that as an empty set
        // would withdraw everything it serves.
        await AnchorHoldsDnsAsync();
        _anchor.Answer = (HttpStatusCode.Conflict, "");

        await SelfWorker().StateAsync(default);

        Assert.Single(_anchor.Asked);
        Assert.False(_book.Answered);
    }

    [Fact]
    public async Task A_member_keeps_asking_while_its_statement_settles()
    {
        await AnchorHoldsDnsAsync();
        _anchor.Answer = (HttpStatusCode.OK, """{"names":[{"name":"jessie.nodes.example.com","kind":"member","key":"","state":"published","note":""}]}""");
        DnsSelfWorker worker = SelfWorker();

        await worker.StateAsync(default);
        int afterStatement = _anchor.Asked.Count;

        // Within the minute after a statement it keeps asking, since the record may still be on its way.
        await worker.StateAsync(default);
        Assert.Equal(afterStatement + 1, _anchor.Asked.Count);
    }

    private sealed class StubAnchor : HttpMessageHandler
    {
        public (HttpStatusCode Status, string Body) Answer { get; set; } = (HttpStatusCode.ServiceUnavailable, "");
        public List<string> Asked { get; } = [];
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked.Add(request.RequestUri!.ToString());
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(Answer.Status)
            {
                Content = new StringContent(Answer.Body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class OneClient(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class NoRequests : ICertificateRequester
    {
        public Task RequestAsync(string name, byte[] csrDer, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class OneServer : IDnsInstanceSource
    {
        public ValueTask<IReadOnlyList<DnsInstance>?> ReadAsync(CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<DnsInstance>?>([new DnsInstance("factorio-1", "factorio", [])]);
    }

    private sealed class CountingBus : IClusterBus
    {
        private readonly List<string> _types = [];

        public int Sent(string type) => _types.Count(t => t == type);

        public Task EnqueueAsync<T>(
            string type, T payload, JsonTypeInfo<T> payloadTypeInfo, IEnumerable<ClusterTarget> targets,
            CancellationToken ct)
        {
            _types.Add(type);
            return Task.CompletedTask;
        }

        public Task EnqueueJsonAsync(string type, string payloadJson, IEnumerable<ClusterTarget> targets, CancellationToken ct)
        {
            _types.Add(type);
            return Task.CompletedTask;
        }
    }
}
