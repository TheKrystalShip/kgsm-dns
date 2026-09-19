using Microsoft.Extensions.Logging.Abstractions;

using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Cluster.Storage;
using TheKrystalShip.KGSM.Dns;
using TheKrystalShip.KGSM.Dns.Anchor.Names;
using TheKrystalShip.KGSM.Dns.Anchor.Registry;

namespace TheKrystalShip.KGSM.Dns.Anchor.Tests;

public sealed class DepartureTests : IDisposable
{
    private static readonly ClusterBases Bases = new("example.com", "nodes", "anchors", "play");
    private const string Self = "dns-anchor";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"kgsm-dns-tests-{Guid.NewGuid():N}");
    private readonly FakeProvider _provider = new();
    private readonly SqliteNameRegistry _registry;
    private readonly RecordReconciler _reconciler;
    private readonly NameService _names;
    private readonly CapabilityNames _capabilities;
    private readonly Departures _departures;
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    public DepartureTests()
    {
        _registry = new SqliteNameRegistry(Path.Combine(_directory, "names.db"));
        _reconciler = new RecordReconciler(
            _registry, _provider, Bases, ttlSeconds: 60, NullLogger<RecordReconciler>.Instance);
        _names = new NameService(
            _registry,
            Bases,
            new MemberNamePool(["walter", "jessie"],
                new Dictionary<string, string> { ["node-a"] = "walter", ["node-b"] = "jessie" }),
            NullLogger<NameService>.Instance);

        // The sweep is handed its roster directly, so the cluster's own store is never opened.
        var options = new ClusterOptions
        {
            MemberId = Self,
            Secret = "",
            StorePath = Path.Combine(_directory, "cluster.db"),
            ReapMs = 300_000,
        };
        var store = new ClusterStore(options, NullLogger<ClusterStore>.Instance);
        _capabilities = new CapabilityNames(_names, _registry, new ClusterStateStore(store, options));
        _departures = new Departures(
            _names, _registry, new MembersStore(store), options, NullLogger<Departures>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static MemberRow Member(string id, string state, DateTimeOffset since, string kind = MemberKind.Node) =>
        MemberRow.New(id, kind) with { MembershipState = state, StateChangedAt = since };

    private void NodeWithServers(string node, string host)
    {
        _registry.StateHost(node, host);
        _names.ClaimMember(node, host);
        _names.ClaimGame(node, "factorio-1", "factorio");
        _names.ClaimGame(node, "valheim", "valheim");
    }

    [Fact]
    public async Task A_removed_node_loses_its_name_its_servers_names_and_their_records()
    {
        NodeWithServers("node-a", "home.ddns.net");
        NodeWithServers("node-b", "friend.ddns.net");
        await _reconciler.RunAsync(CancellationToken.None);
        Assert.NotNull(_provider.Find(Bases.Compose(NameKind.Member, "walter")));

        IReadOnlySet<string> departed = _departures.Sweep(
            [Member("node-a", GossipState.Left, _now.AddMinutes(-5)), Member("node-b", GossipState.Alive, _now)],
            Self, _now);
        await _reconciler.RunAsync(CancellationToken.None);

        Assert.Equal(["node-a"], departed);
        Assert.Empty(_registry.ForMember("node-a"));
        Assert.Null(_registry.HostOf("node-a"));
        Assert.Null(_provider.Find(Bases.Compose(NameKind.Member, "walter")));
        Assert.Null(_provider.Find(Bases.Compose(NameKind.Game, "factorio")));

        // Only the node that left: the other keeps everything it holds.
        Assert.Equal(3, _registry.ForMember("node-b").Count);
        Assert.NotNull(_provider.Find(Bases.Compose(NameKind.Member, "jessie")));
    }

    [Fact]
    public void A_removal_still_inside_the_settle_window_releases_nothing()
    {
        // A running member refutes a removal within a gossip round, and the mesh overturns it. Releasing on
        // first sight would withdraw names a member goes on serving.
        NodeWithServers("node-a", "home.ddns.net");

        IReadOnlySet<string> departed = _departures.Sweep(
            [Member("node-a", GossipState.Left, _now.AddSeconds(-5))], Self, _now);

        Assert.Empty(departed);
        Assert.Equal(3, _registry.ForMember("node-a").Count);
        Assert.Equal("home.ddns.net", _registry.HostOf("node-a"));
    }

    [Fact]
    public void An_offline_member_keeps_every_name_it_holds()
    {
        // Dead is what a member that stopped answering becomes, and it is reaped the same way a departed
        // one is. Only a removal is a departure.
        NodeWithServers("node-a", "home.ddns.net");

        IReadOnlySet<string> departed = _departures.Sweep(
            [Member("node-a", GossipState.Dead, _now.AddHours(-1)), Member("node-b", GossipState.Suspect, _now.AddHours(-1))],
            Self, _now);

        Assert.Empty(departed);
        Assert.Equal(3, _registry.ForMember("node-a").Count);
    }

    [Fact]
    public async Task A_capability_left_assigned_to_a_removed_member_is_withdrawn_not_left_pointing_at_it()
    {
        _registry.StateHost("bot-anchor", "home.ddns.net");
        ClusterAssignment bot = new("bot", "bot-anchor", Version: 1, SetBy: "bot-anchor");
        _capabilities.Sync([bot], Self, "home.ddns.net");
        await _reconciler.RunAsync(CancellationToken.None);
        string name = Bases.Compose(NameKind.Capability, "bot");
        Assert.NotNull(_provider.Find(name));

        _departures.Sweep([Member("bot-anchor", GossipState.Left, _now.AddMinutes(-5), MemberKind.Anchor)], Self, _now);
        _capabilities.Sync([bot], Self, "home.ddns.net");
        await _reconciler.RunAsync(CancellationToken.None);

        // The assignment still names it, and nobody else serves the capability: the name answers nobody.
        Assert.Equal(NameState.Blocked, _registry.Find(name)!.State);
        Assert.Null(_provider.Find(name));
    }

    [Fact]
    public void The_settle_window_stays_inside_a_short_departure_window()
    {
        var options = new ClusterOptions
        {
            MemberId = Self, Secret = "", StorePath = Path.Combine(_directory, "c2.db"), ReapMs = 10_000, LeftReapMs = 10_000,
        };
        var departures = new Departures(
            _names, _registry, new MembersStore(new ClusterStore(options, NullLogger<ClusterStore>.Instance)),
            options, NullLogger<Departures>.Instance);

        Assert.Equal(TimeSpan.FromSeconds(5), departures.Settle);
        Assert.Equal(TimeSpan.FromMinutes(3), _departures.Settle);
    }

    [Fact]
    public void A_member_that_comes_back_keeps_what_it_reclaims_until_it_is_removed_again()
    {
        NodeWithServers("node-a", "home.ddns.net");
        MemberRow removed = Member("node-a", GossipState.Left, _now.AddMinutes(-5)) with { Incarnation = 13 };
        Assert.Equal(["node-a"], _departures.Sweep([removed], Self, _now));

        // It starts again and states itself before its refutation has reached this anchor: the row still
        // says the same removal, and the names it just reclaimed stay.
        NodeWithServers("node-a", "home.ddns.net");
        Assert.Empty(_departures.Sweep([removed], Self, _now.AddMinutes(1)));
        Assert.Equal(3, _registry.ForMember("node-a").Count);

        // An anchor restarted in between reads the removal it acted on from its own store.
        var restarted = new Departures(
            _names, _registry,
            new MembersStore(new ClusterStore(
                new ClusterOptions { MemberId = Self, Secret = "", StorePath = Path.Combine(_directory, "c3.db") },
                NullLogger<ClusterStore>.Instance)),
            new ClusterOptions { MemberId = Self, Secret = "", StorePath = Path.Combine(_directory, "c3.db") },
            NullLogger<Departures>.Instance);
        Assert.Empty(restarted.Sweep([removed], Self, _now.AddMinutes(2)));
        Assert.Equal(3, _registry.ForMember("node-a").Count);

        // Removed again, one incarnation above its refutation: a new removal, acted on.
        MemberRow again = removed with { Incarnation = 15, StateChangedAt = _now.AddMinutes(2) };
        Assert.Equal(["node-a"], _departures.Sweep([again], Self, _now.AddMinutes(6)));
        Assert.Empty(_registry.ForMember("node-a"));
    }
}
