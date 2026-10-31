using Microsoft.Extensions.Logging.Abstractions;

using TheKrystalShip.KGSM.Dns;
using TheKrystalShip.KGSM.Dns.Anchor.Names;
using TheKrystalShip.KGSM.Dns.Anchor.Providers;
using TheKrystalShip.KGSM.Dns.Anchor.Registry;

namespace TheKrystalShip.KGSM.Dns.Anchor.Tests;

public sealed class ReconcilerTests : IDisposable
{
    private static readonly ClusterBases Bases = new("example.com", "nodes", "anchors", "play");

    private readonly string _registryPath =
        Path.Combine(Path.GetTempPath(), $"kgsm-dns-tests-{Guid.NewGuid():N}", "names.db");

    private readonly FakeProvider _provider = new();
    private readonly SqliteNameRegistry _registry;
    private readonly RecordReconciler _reconciler;
    private readonly NameService _names;

    public ReconcilerTests()
    {
        _registry = new SqliteNameRegistry(_registryPath);
        _reconciler = new RecordReconciler(
            _registry, _provider, Bases, ttlSeconds: 60, NullLogger<RecordReconciler>.Instance);
        _names = new NameService(
            _registry,
            Bases,
            new MemberNamePool(["jessie", "walter"], new Dictionary<string, string>()),
            NullLogger<NameService>.Instance);
    }

    public void Dispose()
    {
        string? directory = Path.GetDirectoryName(_registryPath);
        if (directory is not null && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task A_claimed_name_is_published_with_its_mark()
    {
        NameRow row = _names.ClaimMember("node-a", "203.0.113.10");

        RecordReconciler.Pass pass = await _reconciler.RunAsync(CancellationToken.None);

        Assert.Equal(1, pass.Published);
        ProviderRecord? published = _provider.Find(row.Name);
        Assert.NotNull(published);
        Assert.Equal("A", published.Type);
        Assert.Equal("203.0.113.10", published.Content);
        Assert.False(published.Proxied);
        Assert.Equal(new RecordMark(NameKind.Member, "node-a", ""), published.Mark);
        Assert.Equal(NameState.Published, _registry.Find(row.Name)!.State);
    }

    [Fact]
    public async Task A_second_pass_with_nothing_changed_writes_nothing()
    {
        _names.ClaimMember("node-a", "203.0.113.10");
        await _reconciler.RunAsync(CancellationToken.None);
        _provider.Calls.Clear();

        RecordReconciler.Pass pass = await _reconciler.RunAsync(CancellationToken.None);

        Assert.True(pass.Quiet);
        Assert.Empty(_provider.Calls);
    }

    [Fact]
    public async Task An_address_that_moves_moves_the_record_and_keeps_the_name()
    {
        NameRow row = _names.ClaimMember("node-a", "203.0.113.10");
        await _reconciler.RunAsync(CancellationToken.None);

        _names.Retarget(_registry.Find(row.Name)!, "198.51.100.7");
        RecordReconciler.Pass pass = await _reconciler.RunAsync(CancellationToken.None);

        Assert.Equal(1, pass.Moved);
        Assert.Equal("198.51.100.7", _provider.Find(row.Name)!.Content);
        Assert.Equal(row.Name, _registry.Find(row.Name)!.Name);
    }

    [Fact]
    public async Task A_record_edited_by_hand_is_brought_back()
    {
        NameRow row = _names.ClaimMember("node-a", "203.0.113.10");
        await _reconciler.RunAsync(CancellationToken.None);

        ProviderRecord published = _provider.Find(row.Name)!;
        await _provider.UpdateAsync(
            published.Id,
            new DesiredRecord(row.Name, "A", "192.0.2.1", 60, published.Comment),
            CancellationToken.None);
        _provider.Calls.Clear();

        await _reconciler.RunAsync(CancellationToken.None);

        Assert.Equal("203.0.113.10", _provider.Find(row.Name)!.Content);
    }

    [Fact]
    public async Task A_record_this_cluster_did_not_make_is_never_touched()
    {
        // The name alone is not evidence: a person is free to make a record that looks exactly like one
        // of ours, and taking it over would be this anchor overwriting somebody's own work.
        var names = new NameService(
            _registry,
            Bases,
            new MemberNamePool(["jessie"], new Dictionary<string, string>()),
            NullLogger<NameService>.Instance);

        string name = Bases.Compose(NameKind.Member, "jessie");
        _provider.Seed(name, "A", "192.0.2.99", comment: "set up by hand");

        NameRow row = names.ClaimMember("node-a", "203.0.113.10");
        Assert.Equal(name, row.Name);

        RecordReconciler.Pass pass = await _reconciler.RunAsync(CancellationToken.None);

        Assert.Equal(1, pass.NewlyContested);
        Assert.Equal([name], pass.Contested);
        Assert.Equal("192.0.2.99", _provider.Find(name)!.Content);
        Assert.Empty(_provider.Calls);
        Assert.Equal(NameState.Blocked, _registry.Find(name)!.State);

        // Contested once, and still held on every pass after: the name stays on the list for as long as
        // the record holding it is there, without being contested again.
        RecordReconciler.Pass next = await _reconciler.RunAsync(CancellationToken.None);
        Assert.Equal(0, next.NewlyContested);
        Assert.Equal([name], next.Contested);
    }

    [Fact]
    public async Task A_pass_that_fails_partway_keeps_what_it_already_changed()
    {
        var names = new NameService(
            _registry,
            Bases,
            new MemberNamePool(["jessie", "walter"], new Dictionary<string, string>()),
            NullLogger<NameService>.Instance);
        names.ClaimMember("node-a", "203.0.113.10");
        names.ClaimMember("node-b", "203.0.113.11");

        _provider.CreatesBeforeOutage = 1;
        await Assert.ThrowsAsync<DnsProviderException>(() => _reconciler.RunAsync(CancellationToken.None));

        // One record reached the zone before the provider stopped answering. It is there whether or not
        // the pass finished, so it is handed over rather than lost with the pass.
        ZoneChange done = Assert.Single(_reconciler.TakeUnreported());
        Assert.Equal(ZoneChangeKind.Published, done.Kind);
        Assert.Empty(_reconciler.TakeUnreported());

        _provider.CreatesBeforeOutage = null;
        RecordReconciler.Pass next = await _reconciler.RunAsync(CancellationToken.None);
        Assert.Single(next.Changes);
    }

    [Fact]
    public async Task A_pass_reports_each_change_with_what_the_name_was()
    {
        NameRow row = _names.ClaimMember("node-a", "203.0.113.10");
        RecordReconciler.Pass first = await _reconciler.RunAsync(CancellationToken.None);

        ZoneChange published = Assert.Single(first.Changes);
        Assert.Equal(ZoneChangeKind.Published, published.Kind);
        Assert.Equal(row.Name, published.Name);
        Assert.Equal("node-a", published.Member);

        _names.Retarget(_registry.Find(row.Name)!, "203.0.113.20");
        RecordReconciler.Pass second = await _reconciler.RunAsync(CancellationToken.None);

        ZoneChange moved = Assert.Single(second.Changes);
        Assert.Equal(ZoneChangeKind.Moved, moved.Kind);
        Assert.Equal("203.0.113.10", moved.From);
        Assert.Equal("203.0.113.20", moved.Content);

        // Released, so the row is gone: what the name was is read off the record's mark.
        _names.Release(row.Name);
        RecordReconciler.Pass third = await _reconciler.RunAsync(CancellationToken.None);

        ZoneChange withdrawn = Assert.Single(third.Changes);
        Assert.Equal(ZoneChangeKind.Withdrawn, withdrawn.Kind);
        Assert.Equal(NameKind.Member, withdrawn.NameKind);
        Assert.Equal("node-a", withdrawn.Member);
    }

    [Fact]
    public async Task A_released_name_is_withdrawn()
    {
        NameRow row = _names.ClaimMember("node-a", "203.0.113.10");
        await _reconciler.RunAsync(CancellationToken.None);

        _names.Release(row.Name);
        RecordReconciler.Pass pass = await _reconciler.RunAsync(CancellationToken.None);

        Assert.Equal(1, pass.Removed);
        Assert.Null(_provider.Find(row.Name));
    }

    [Fact]
    public async Task An_unmarked_record_survives_a_release_sweep()
    {
        _provider.Seed("mail.example.com", "A", "192.0.2.25");
        _provider.Seed(Bases.Compose(NameKind.Game, "ours"), "A", "192.0.2.26", "made by hand");

        await _reconciler.RunAsync(CancellationToken.None);

        Assert.NotNull(_provider.Find("mail.example.com"));
        Assert.NotNull(_provider.Find(Bases.Compose(NameKind.Game, "ours")));
    }

    [Fact]
    public async Task An_empty_registry_is_rebuilt_from_the_marks()
    {
        NameRow row = _names.ClaimMember("node-a", "203.0.113.10");
        await _reconciler.RunAsync(CancellationToken.None);

        // What taking the capability over looks like: the zone still says everything, and nothing has
        // to move from the member that published it. A second reconciler is the second member.
        _registry.Remove(row.Name);
        var successor = new RecordReconciler(
            _registry, _provider, Bases, ttlSeconds: 60, NullLogger<RecordReconciler>.Instance);

        RecordReconciler.Pass pass = await successor.RunAsync(CancellationToken.None);

        Assert.Equal(1, pass.Recovered);
        NameRow recovered = _registry.Find(row.Name)!;
        Assert.Equal("node-a", recovered.Member);
        Assert.Equal(NameKind.Member, recovered.Kind);
        Assert.Equal("203.0.113.10", recovered.Content);
    }

    [Fact]
    public async Task A_holder_that_lost_its_store_keeps_the_name_the_zone_gives_it()
    {
        // The order a holder runs in: read the zone, then claim. Claiming first draws a new name for a
        // member the zone already names, and the pass after withdraws the one it is reached at.
        NameRow original = _names.ClaimMember("node-a", "203.0.113.10");
        await _reconciler.RunAsync(CancellationToken.None);

        _registry.Remove(original.Name);
        var successor = new RecordReconciler(
            _registry, _provider, Bases, ttlSeconds: 60, NullLogger<RecordReconciler>.Instance);

        await successor.EnsureReadyAsync(CancellationToken.None);
        NameRow claimed = _names.ClaimMember("node-a", "203.0.113.10");
        RecordReconciler.Pass pass = await successor.RunAsync(CancellationToken.None);

        Assert.Equal(original.Name, claimed.Name);
        Assert.Equal(1, pass.Recovered);
        Assert.Equal(0, pass.Removed);
        Assert.NotNull(_provider.Find(original.Name));
    }

    [Fact]
    public async Task Releasing_the_last_name_does_not_read_as_an_unbuilt_registry()
    {
        // The two states look identical — an empty registry beside a marked record — so an anchor that
        // recovered on every pass would rebuild the row it is about to withdraw, and a name given up
        // would never go away.
        NameRow row = _names.ClaimMember("node-a", "203.0.113.10");
        await _reconciler.RunAsync(CancellationToken.None);

        _names.Release(row.Name);
        RecordReconciler.Pass pass = await _reconciler.RunAsync(CancellationToken.None);

        Assert.Equal(0, pass.Recovered);
        Assert.Equal(1, pass.Removed);
        Assert.Null(_registry.Find(row.Name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("192.168.1.128")]
    [InlineData("100.72.0.5")]
    [InlineData("dns.anchors.example.com")]
    public async Task A_host_that_cannot_be_reached_from_outside_publishes_nothing(string host)
    {
        // Unknown, private, behind carrier-grade NAT, or one of this cluster's own names: each would be
        // a record that resolves for nobody, or for nothing but itself.
        NameRow row = _names.ClaimMember("node-a", host);

        RecordReconciler.Pass pass = await _reconciler.RunAsync(CancellationToken.None);

        Assert.Equal(0, pass.Published);
        Assert.Null(_provider.Find(row.Name));
        Assert.Equal(NameState.Blocked, _registry.Find(row.Name)!.State);
        Assert.NotEmpty(_registry.Find(row.Name)!.Note);
    }

    [Fact]
    public async Task A_node_on_a_dynamic_address_is_an_alias_of_its_dynamic_dns_name()
    {
        // A home connection's address moves whenever the provider likes, and the network's own
        // dynamic-DNS client already follows it. Pointing at that name means the cluster's record never
        // has to move when the address does.
        NameRow row = _names.ClaimMember("node-a", "Home.DDNS.net.");
        await _reconciler.RunAsync(CancellationToken.None);

        ProviderRecord published = _provider.Find(row.Name)!;
        Assert.Equal("CNAME", published.Type);
        Assert.Equal("home.ddns.net", published.Content);
    }

    [Fact]
    public async Task A_capability_points_at_its_holders_host()
    {
        NameRow capability = _names.ClaimCapability("assistant", "anchor-a", "home.ddns.net");
        await _reconciler.RunAsync(CancellationToken.None);

        Assert.Equal(Bases.Compose(NameKind.Capability, "assistant"), capability.Name);
        ProviderRecord published = _provider.Find(capability.Name)!;
        Assert.Equal("CNAME", published.Type);
        Assert.Equal("home.ddns.net", published.Content);
    }

    [Fact]
    public void An_anchor_holds_no_node_name()
    {
        // A machine running a node and several anchors carries one node name, not one per member.
        _names.ClaimCapability("dns", "anchor-a", "home.ddns.net");

        Assert.Empty(_registry.OfKind(NameKind.Member));
    }

    [Fact]
    public async Task Reassigning_a_capability_moves_its_name_to_the_new_holder()
    {
        _names.ClaimCapability("assistant", "anchor-a", "home.ddns.net");
        await _reconciler.RunAsync(CancellationToken.None);

        NameRow moved = _names.ClaimCapability("assistant", "anchor-b", "friend.ddns.net");
        await _reconciler.RunAsync(CancellationToken.None);

        Assert.Equal("anchor-b", moved.Member);
        Assert.Equal("friend.ddns.net", _provider.Find(moved.Name)!.Content);
        Assert.Equal(new RecordMark(NameKind.Capability, "anchor-b", "assistant"), _provider.Find(moved.Name)!.Mark);
    }

    [Fact]
    public void Nodes_claiming_at_once_never_share_a_name()
    {
        // Claims arrive from the inbox and the worker concurrently. A draw that read and wrote without
        // holding anything would let two of them see one label free, and the second would overwrite the
        // first's row.
        var names = new NameService(
            _registry,
            Bases,
            new MemberNamePool(["walter", "skyler", "hank", "marie", "saul", "gustavo", "mike", "tuco"],
                new Dictionary<string, string>()),
            NullLogger<NameService>.Instance);

        NameRow[] claimed = new NameRow[8];
        Parallel.For(0, claimed.Length, i => claimed[i] = names.ClaimMember("node-" + i, "home.ddns.net"));

        Assert.Equal(claimed.Length, claimed.Select(r => r.Name).Distinct().Count());
        Assert.Equal(claimed.Length, _registry.OfKind(NameKind.Member).Count);
    }

    [Fact]
    public void A_name_pinned_to_another_member_is_never_drawn()
    {
        // The first node to join must not draw the name somebody else is already promised.
        var names = new NameService(
            _registry,
            Bases,
            new MemberNamePool(["walter", "skyler"], new Dictionary<string, string> { ["hotrod"] = "walter" }),
            NullLogger<NameService>.Instance);

        for (int i = 0; i < 20; i++)
        {
            NameRow drawn = names.ClaimMember("node-" + i, "203.0.113.10");
            Assert.NotEqual(Bases.Compose(NameKind.Member, "walter"), drawn.Name);
            names.Release(drawn.Name);
        }

        Assert.Equal(Bases.Compose(NameKind.Member, "walter"), names.ClaimMember("hotrod", "home.ddns.net").Name);
    }

    [Fact]
    public void A_member_that_already_has_a_name_keeps_it()
    {
        NameRow first = _names.ClaimMember("node-a", "203.0.113.10");
        NameRow again = _names.ClaimMember("node-a", "198.51.100.7");

        Assert.Equal(first.Name, again.Name);
        Assert.Equal("198.51.100.7", again.Content);
    }

    [Fact]
    public void Two_members_never_share_a_name()
    {
        NameRow first = _names.ClaimMember("node-a", "203.0.113.10");
        NameRow second = _names.ClaimMember("node-b", "198.51.100.7");

        Assert.NotEqual(first.Name, second.Name);
    }

    [Fact]
    public void A_pinned_member_keeps_the_name_it_already_answers_to()
    {
        var names = new NameService(
            _registry,
            Bases,
            new MemberNamePool(["walter"], new Dictionary<string, string> { ["node-a"] = "hotrod" }),
            NullLogger<NameService>.Instance);

        Assert.Equal(Bases.Compose(NameKind.Member, "hotrod"), names.ClaimMember("node-a", "203.0.113.10").Name);
    }

    [Fact]
    public void An_exhausted_list_blocks_rather_than_spelling_out_the_machine()
    {
        var names = new NameService(
            _registry,
            Bases,
            new MemberNamePool(["jessie"], new Dictionary<string, string>()),
            NullLogger<NameService>.Instance);

        names.ClaimMember("node-a", "203.0.113.10");
        NameRow second = names.ClaimMember("node-b", "198.51.100.7");

        Assert.Equal(NameState.Blocked, second.State);
        Assert.NotEmpty(second.Note);
    }
}
