using System.Net;

using Microsoft.Extensions.Logging.Abstractions;

using TheKrystalShip.KGSM.Dns;
using TheKrystalShip.KGSM.Dns.Anchor.Names;
using TheKrystalShip.KGSM.Dns.Anchor.Registry;
using TheKrystalShip.KGSM.Dns.Messages;

namespace TheKrystalShip.KGSM.Dns.Anchor.Tests;

public sealed class PortCollisionTests : IDisposable
{
    private static readonly ClusterBases Bases = new("example.com", "nodes", "anchors", "play");

    private readonly string _registryPath =
        Path.Combine(Path.GetTempPath(), $"kgsm-dns-tests-{Guid.NewGuid():N}", "names.db");

    private readonly SqliteNameRegistry _registry;
    private readonly NameService _names;
    private readonly FakeResolver _resolver = new();
    private readonly PortCollisions _collisions;

    public PortCollisionTests()
    {
        _registry = new SqliteNameRegistry(_registryPath);
        _names = new NameService(
            _registry,
            Bases,
            new MemberNamePool(["walter", "jessie", "skyler"], new Dictionary<string, string>()),
            NullLogger<NameService>.Instance);
        _collisions = new PortCollisions(_registry, _resolver);

        // Two nodes behind one router, a third on its own line.
        _resolver.Map["home-a.ddns.net"] = [IPAddress.Parse("203.0.113.10")];
        _resolver.Map["home-b.ddns.net"] = [IPAddress.Parse("203.0.113.10")];
        _resolver.Map["away.ddns.net"] = [IPAddress.Parse("198.51.100.7")];
    }

    public void Dispose()
    {
        string? directory = Path.GetDirectoryName(_registryPath);
        if (directory is not null && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private void Node(string member, string host)
    {
        _registry.StateHost(member, host);
        _names.ClaimMember(member, host);
    }

    private string Server(string member, string instance, string blueprint, params DnsPort[] ports)
    {
        NameRow row = _names.ClaimGame(member, instance, blueprint);
        _registry.SetPorts(row.Name, ports);
        return row.Name;
    }

    private static DnsPort Udp(int start, int end = 0) => new(start, end == 0 ? start : end, "udp");

    private static DnsPort Tcp(int start, int end = 0) => new(start, end == 0 ? start : end, "tcp");

    [Fact]
    public async Task Servers_on_two_nodes_behind_one_address_wanting_one_port_are_flagged_on_both()
    {
        Node("node-a", "home-a.ddns.net");
        Node("node-b", "home-b.ddns.net");
        string a = Server("node-a", "mc-1", "minecraft", Tcp(25565));
        string b = Server("node-b", "mc-2", "minecraft", Tcp(25565));

        IReadOnlySet<string> touched = await _collisions.RefreshAsync(CancellationToken.None);

        Assert.Equal([new DnsCollision(b, "node-b", 25565, 25565, "tcp")], _collisions.For(a));
        Assert.Equal([new DnsCollision(a, "node-a", 25565, 25565, "tcp")], _collisions.For(b));
        Assert.Equal(["node-a", "node-b"], touched.Order());
    }

    [Fact]
    public async Task Overlapping_ranges_report_the_ports_both_want()
    {
        Node("node-a", "home-a.ddns.net");
        Node("node-b", "home-b.ddns.net");
        string a = Server("node-a", "valheim-1", "valheim", Udp(2456, 2458));
        Server("node-b", "other-1", "stationeers", Udp(2458, 2460));

        await _collisions.RefreshAsync(CancellationToken.None);

        DnsCollision only = Assert.Single(_collisions.For(a)!);
        Assert.Equal((2458, 2458, "udp"), (only.Start, only.End, only.Protocol));
    }

    [Fact]
    public async Task The_same_port_on_another_protocol_is_no_collision()
    {
        Node("node-a", "home-a.ddns.net");
        Node("node-b", "home-b.ddns.net");
        string a = Server("node-a", "a-1", "factorio", Udp(34197));
        Server("node-b", "b-1", "terraria", Tcp(34197));

        await _collisions.RefreshAsync(CancellationToken.None);

        Assert.Empty(_collisions.For(a)!);
    }

    [Fact]
    public async Task Nodes_on_different_addresses_never_collide()
    {
        Node("node-a", "home-a.ddns.net");
        Node("node-c", "away.ddns.net");
        string a = Server("node-a", "mc-1", "minecraft", Tcp(25565));
        string c = Server("node-c", "mc-2", "minecraft", Tcp(25565));

        await _collisions.RefreshAsync(CancellationToken.None);

        Assert.Empty(_collisions.For(a)!);
        Assert.Empty(_collisions.For(c)!);
    }

    [Fact]
    public async Task Two_servers_on_one_node_are_the_engines_to_answer_for()
    {
        Node("node-a", "home-a.ddns.net");
        string a = Server("node-a", "mc-1", "minecraft", Tcp(25565));
        Server("node-a", "mc-2", "minecraft", Tcp(25565));

        await _collisions.RefreshAsync(CancellationToken.None);

        Assert.Empty(_collisions.For(a)!);
    }

    [Fact]
    public async Task A_host_that_cannot_be_resolved_is_unknown_not_clear()
    {
        Node("node-a", "home-a.ddns.net");
        Node("node-b", "gone.ddns.net");
        string a = Server("node-a", "mc-1", "minecraft", Tcp(25565));
        string b = Server("node-b", "mc-2", "minecraft", Tcp(25565));

        await _collisions.RefreshAsync(CancellationToken.None);

        Assert.Null(_collisions.For(b));
        Assert.Null(_collisions.For(a));
    }

    [Fact]
    public async Task A_node_stating_no_public_host_shares_no_address()
    {
        Node("node-a", "home-a.ddns.net");
        Node("node-b", "");
        string a = Server("node-a", "mc-1", "minecraft", Tcp(25565));
        Server("node-b", "mc-2", "minecraft", Tcp(25565));

        await _collisions.RefreshAsync(CancellationToken.None);

        Assert.Empty(_collisions.For(a)!);
    }

    [Fact]
    public async Task Ports_this_anchor_was_never_told_are_unknown()
    {
        // After the capability moves the names come back from the zone, the ports do not.
        Node("node-a", "home-a.ddns.net");
        Node("node-b", "home-b.ddns.net");
        string a = Server("node-a", "mc-1", "minecraft", Tcp(25565));
        _names.ClaimGame("node-b", "mc-2", "minecraft");

        await _collisions.RefreshAsync(CancellationToken.None);

        Assert.Null(_collisions.For(a));
    }

    [Fact]
    public async Task A_check_that_changes_nothing_tells_nobody()
    {
        Node("node-a", "home-a.ddns.net");
        Node("node-b", "home-b.ddns.net");
        Server("node-a", "mc-1", "minecraft", Tcp(25565));
        Server("node-b", "mc-2", "minecraft", Tcp(25565));
        await _collisions.RefreshAsync(CancellationToken.None);

        Assert.Empty(await _collisions.RefreshAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_node_moving_off_the_shared_address_clears_both_sides()
    {
        Node("node-a", "home-a.ddns.net");
        Node("node-b", "home-b.ddns.net");
        string a = Server("node-a", "mc-1", "minecraft", Tcp(25565));
        string b = Server("node-b", "mc-2", "minecraft", Tcp(25565));
        await _collisions.RefreshAsync(CancellationToken.None);

        _resolver.Map["home-b.ddns.net"] = [IPAddress.Parse("198.51.100.99")];
        IReadOnlySet<string> touched = await _collisions.RefreshAsync(CancellationToken.None);

        Assert.Empty(_collisions.For(a)!);
        Assert.Empty(_collisions.For(b)!);
        Assert.Equal(["node-a", "node-b"], touched.Order());
    }

    [Fact]
    public void Ports_survive_the_registry_round_trip_and_leave_with_the_name()
    {
        Node("node-a", "home-a.ddns.net");
        string a = Server("node-a", "valheim-1", "valheim", Udp(2456, 2458), Tcp(80));

        Assert.Equal([Udp(2456, 2458), Tcp(80)], _registry.AllPorts()[a]);

        _names.ReleaseGamesExcept("node-a", new HashSet<string>());
        Assert.False(_registry.AllPorts().ContainsKey(a));
    }

    private sealed class FakeResolver : IHostResolver
    {
        public Dictionary<string, IReadOnlyList<IPAddress>> Map { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<IPAddress>?> ResolveAsync(string host, CancellationToken ct) =>
            Task.FromResult(Map.TryGetValue(host, out IReadOnlyList<IPAddress>? a) ? a : null);
    }
}
