using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Cluster.Messaging;
using TheKrystalShip.KGSM.Cluster.Storage;
using TheKrystalShip.KGSM.Dns.Member;
using TheKrystalShip.KGSM.Dns.Messages;
using TheKrystalShip.KGSM.Dns.Serving;

namespace TheKrystalShip.KGSM.Dns.Tests;

/// <summary>
/// A member asks for a certificate ahead of its schedule when the member holding <c>dns</c> tells it to,
/// and only for a name it holds published.
/// </summary>
public sealed class RenewTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"kgsm-dns-renew-{Guid.NewGuid():N}");
    private readonly ClusterStateStore _state;
    private readonly MembersStore _members;
    private readonly DnsNameBook _book = new();
    private readonly RecordingRequester _requester = new();
    private readonly DnsRenewHandler _handler;

    public RenewTests()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "tls"));
        ClusterOptions cluster = new ClusterOptions
        {
            MemberId = "node-a",
            Secret = "test-secret-long-enough-to-sign-with-0123456789",
            StorePath = Path.Combine(_directory, "cluster.db"),
        }.Validate();
        var store = new ClusterStore(cluster, NullLogger<ClusterStore>.Instance);
        _state = new ClusterStateStore(store, cluster);
        _members = new MembersStore(store);

        var files = new CertificateFiles(Path.Combine(_directory, "tls"));
        var keeper = new CertificateKeeper(
            _book, files,
            new NginxSite(new NginxSiteOptions("kgsm-api", Path.Combine(_directory, "sites"), "/nonexistent.locations"),
                files, NullLogger<NginxSite>.Instance),
            _requester, NullLogger<CertificateKeeper>.Instance);
        _handler = new DnsRenewHandler(_state, keeper, NullLogger<DnsRenewHandler>.Instance);

        _book.Replace([
            new DnsNameEntry("walter.nodes.example.com", NameKinds.Spell(NameKind.Member), "", "published", ""),
            new DnsNameEntry("bot.anchors.example.com", NameKinds.Spell(NameKind.Capability), "bot", "pending", ""),
        ]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private async Task AnchorHoldsDnsAsync()
    {
        await _members.UpsertAsync(MemberRow.New("dns-anchor", MemberKind.Anchor), default);
        Assert.True(await _state.TryClaimAsync(DnsCapability.Name, "dns-anchor", default));
    }

    private static ClusterEnvelope Renew(string from, string name) => new(
        Guid.NewGuid().ToString("N"), DnsMessageTypes.Renew, from, DateTimeOffset.UtcNow,
        JsonSerializer.SerializeToElement(new DnsRenew(name), DnsJsonContext.Default.DnsRenew));

    [Fact]
    public async Task A_renewal_from_the_anchor_asks_for_a_published_name_now()
    {
        await AnchorHoldsDnsAsync();

        await _handler.HandleAsync(Renew("dns-anchor", "walter.nodes.example.com"), default);

        Assert.Equal(["walter.nodes.example.com"], _requester.Asked);
    }

    [Fact]
    public async Task A_renewal_from_a_member_not_holding_dns_is_ignored()
    {
        await AnchorHoldsDnsAsync();

        await _handler.HandleAsync(Renew("node-b", "walter.nodes.example.com"), default);

        Assert.Empty(_requester.Asked);
    }

    [Fact]
    public async Task A_renewal_for_a_name_not_published_here_is_ignored()
    {
        // The anchor refuses a signing request for a name whose record is not yet written, and a name this
        // member does not hold is one it has no business asking about.
        await AnchorHoldsDnsAsync();

        await _handler.HandleAsync(Renew("dns-anchor", "bot.anchors.example.com"), default);
        await _handler.HandleAsync(Renew("dns-anchor", "jessie.nodes.example.com"), default);

        Assert.Empty(_requester.Asked);
    }

    private sealed class RecordingRequester : ICertificateRequester
    {
        public List<string> Asked { get; } = [];

        public Task RequestAsync(string name, byte[] csrDer, CancellationToken ct)
        {
            Asked.Add(name);
            return Task.CompletedTask;
        }
    }
}
