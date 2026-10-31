using Microsoft.Extensions.Logging.Abstractions;

using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Cluster.Storage;
using TheKrystalShip.KGSM.Dns;
using TheKrystalShip.KGSM.Dns.Anchor.Names;
using TheKrystalShip.KGSM.Dns.Anchor.Providers;
using TheKrystalShip.KGSM.Dns.Anchor.Registry;

namespace TheKrystalShip.KGSM.Dns.Anchor.Tests;

public sealed class CapabilityNameTests : IDisposable
{
    private static readonly ClusterBases Bases = new("example.com", "nodes", "anchors", "play");
    private const string Self = "dns-anchor";
    private const string SelfHost = "home.ddns.net";

    private readonly string _registryPath =
        Path.Combine(Path.GetTempPath(), $"kgsm-dns-tests-{Guid.NewGuid():N}", "names.db");

    private readonly FakeProvider _provider = new();
    private readonly SqliteNameRegistry _registry;
    private readonly RecordReconciler _reconciler;
    private readonly CapabilityNames _capabilities;

    public CapabilityNameTests()
    {
        _registry = new SqliteNameRegistry(_registryPath);
        _reconciler = new RecordReconciler(
            _registry, _provider, Bases, ttlSeconds: 60, NullLogger<RecordReconciler>.Instance);
        var names = new NameService(
            _registry,
            Bases,
            new MemberNamePool(["walter"], new Dictionary<string, string>()),
            NullLogger<NameService>.Instance);
        _capabilities = new CapabilityNames(names, _registry, UnreadState());
    }

    // Sync is handed its assignments directly, so the cluster's own store is never opened.
    private ClusterStateStore UnreadState()
    {
        var options = new ClusterOptions
        {
            MemberId = Self,
            Secret = "",
            StorePath = Path.Combine(Path.GetDirectoryName(_registryPath)!, "cluster.db"),
        };
        return new ClusterStateStore(new ClusterStore(options, NullLogger<ClusterStore>.Instance), options);
    }

    public void Dispose()
    {
        string? directory = Path.GetDirectoryName(_registryPath);
        if (directory is not null && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private static ClusterAssignment Held(string capability, string member) =>
        new(capability, member, Version: 1, SetBy: member);

    private IReadOnlySet<string> Sync(params ClusterAssignment[] assignments) =>
        _capabilities.Sync(assignments, Self, SelfHost);

    private static string NameOf(string capability) => Bases.Compose(NameKind.Capability, capability);

    [Fact]
    public async Task Every_held_capability_is_named_at_the_host_its_holder_stated()
    {
        _registry.StateHost("assistant-anchor", "friend.ddns.net");

        Sync(Held("dns", Self), Held("assistant", "assistant-anchor"));
        await _reconciler.RunAsync(CancellationToken.None);

        Assert.Equal(SelfHost, _provider.Find(NameOf("dns"))!.Content);
        Assert.Equal("friend.ddns.net", _provider.Find(NameOf("assistant"))!.Content);
        Assert.Equal(new RecordMark(NameKind.Capability, "assistant-anchor", "assistant"), _provider.Find(NameOf("assistant"))!.Mark);
    }

    [Fact]
    public async Task A_holder_that_has_not_said_where_it_is_reached_publishes_nothing()
    {
        Sync(Held("dns", Self), Held("assistant", "assistant-anchor"));
        await _reconciler.RunAsync(CancellationToken.None);

        NameRow row = _registry.Find(NameOf("assistant"))!;
        Assert.Equal(NameState.Blocked, row.State);
        Assert.NotEmpty(row.Note);
        Assert.Null(_provider.Find(NameOf("assistant")));
    }

    [Fact]
    public async Task A_holder_stating_it_has_no_public_host_publishes_nothing()
    {
        _registry.StateHost("assistant-anchor", "");

        Sync(Held("dns", Self), Held("assistant", "assistant-anchor"));
        await _reconciler.RunAsync(CancellationToken.None);

        Assert.Equal(NameState.Blocked, _registry.Find(NameOf("assistant"))!.State);
        Assert.Null(_provider.Find(NameOf("assistant")));
    }

    [Fact]
    public async Task A_holder_this_anchor_has_not_heard_from_keeps_the_name_it_already_holds()
    {
        // The anchor that rebuilds its registry from the zone after taking the capability has heard from
        // nobody yet. Its first pass must not withdraw every name the cluster is reached at.
        _registry.StateHost("assistant-anchor", "friend.ddns.net");
        Sync(Held("dns", Self), Held("assistant", "assistant-anchor"));
        await _reconciler.RunAsync(CancellationToken.None);

        var rebuilt = new SqliteNameRegistry(Path.Combine(
            Path.GetDirectoryName(_registryPath)!, "rebuilt.db"));
        var reconciler = new RecordReconciler(
            rebuilt, _provider, Bases, ttlSeconds: 60, NullLogger<RecordReconciler>.Instance);
        await reconciler.EnsureReadyAsync(CancellationToken.None);
        var capabilities = new CapabilityNames(
            new NameService(rebuilt, Bases, new MemberNamePool(["walter"], new Dictionary<string, string>()),
                NullLogger<NameService>.Instance),
            rebuilt,
            UnreadState());

        capabilities.Sync([Held("dns", Self), Held("assistant", "assistant-anchor")], Self, SelfHost);
        RecordReconciler.Pass pass = await reconciler.RunAsync(CancellationToken.None);

        Assert.Equal(0, pass.Removed);
        Assert.Equal("friend.ddns.net", _provider.Find(NameOf("assistant"))!.Content);
    }

    [Fact]
    public async Task Reassigning_moves_the_name_and_tells_both_holders()
    {
        _registry.StateHost("node-a", "home.ddns.net");
        _registry.StateHost("node-b", "friend.ddns.net");
        Sync(Held("dns", Self), Held("probe", "node-a"));
        await _reconciler.RunAsync(CancellationToken.None);

        IReadOnlySet<string> touched = Sync(Held("dns", Self), Held("probe", "node-b"));
        await _reconciler.RunAsync(CancellationToken.None);

        Assert.Equal(["node-a", "node-b"], touched.Order());
        Assert.Equal("node-b", _registry.Find(NameOf("probe"))!.Member);
        Assert.Equal("friend.ddns.net", _provider.Find(NameOf("probe"))!.Content);
    }

    [Fact]
    public async Task Moving_to_a_holder_that_has_not_said_where_it_is_reached_withdraws_the_name()
    {
        _registry.StateHost("node-a", "home.ddns.net");
        Sync(Held("dns", Self), Held("probe", "node-a"));
        await _reconciler.RunAsync(CancellationToken.None);

        Sync(Held("dns", Self), Held("probe", "node-b"));
        await _reconciler.RunAsync(CancellationToken.None);

        Assert.Equal("node-b", _registry.Find(NameOf("probe"))!.Member);
        Assert.Null(_provider.Find(NameOf("probe")));
    }

    [Fact]
    public async Task A_capability_held_by_nobody_loses_its_name()
    {
        _registry.StateHost("node-a", "home.ddns.net");
        Sync(Held("dns", Self), Held("probe", "node-a"));
        await _reconciler.RunAsync(CancellationToken.None);

        IReadOnlySet<string> touched = Sync(Held("dns", Self), new ClusterAssignment("probe", "", 2, "node-a"));
        await _reconciler.RunAsync(CancellationToken.None);

        Assert.Contains("node-a", touched);
        Assert.Null(_registry.Find(NameOf("probe")));
        Assert.Null(_provider.Find(NameOf("probe")));
    }

    [Fact]
    public async Task A_holder_changing_its_host_moves_the_record()
    {
        _registry.StateHost("assistant-anchor", "friend.ddns.net");
        Sync(Held("dns", Self), Held("assistant", "assistant-anchor"));
        await _reconciler.RunAsync(CancellationToken.None);

        _registry.StateHost("assistant-anchor", "elsewhere.ddns.net");
        IReadOnlySet<string> touched = Sync(Held("dns", Self), Held("assistant", "assistant-anchor"));
        await _reconciler.RunAsync(CancellationToken.None);

        Assert.Equal(["assistant-anchor"], touched);
        Assert.Equal("elsewhere.ddns.net", _provider.Find(NameOf("assistant"))!.Content);
    }

    [Fact]
    public void Nothing_changed_touches_nobody()
    {
        _registry.StateHost("assistant-anchor", "friend.ddns.net");
        Sync(Held("dns", Self), Held("assistant", "assistant-anchor"));

        Assert.Empty(Sync(Held("dns", Self), Held("assistant", "assistant-anchor")));
    }

    [Fact]
    public void Never_stated_and_stated_none_are_different_answers()
    {
        Assert.Null(_registry.HostOf("assistant-anchor"));

        _registry.StateHost("assistant-anchor", "");

        Assert.Equal("", _registry.HostOf("assistant-anchor"));
        Assert.Equal(["assistant-anchor"], _registry.StatedMembers());
    }
}
