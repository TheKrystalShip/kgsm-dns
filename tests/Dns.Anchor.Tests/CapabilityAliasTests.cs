using Microsoft.Extensions.Logging.Abstractions;

using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Cluster.Storage;
using TheKrystalShip.KGSM.Dns;
using TheKrystalShip.KGSM.Dns.Anchor.Names;
using TheKrystalShip.KGSM.Dns.Anchor.Providers;
using TheKrystalShip.KGSM.Dns.Anchor.Registry;

namespace TheKrystalShip.KGSM.Dns.Anchor.Tests;

/// <summary>
/// A capability alias is an administrator's name for a capability, one label directly under the zone. It
/// resolves where the capability's own name does, follows the capability between holders, and lives beside
/// names the operator made by hand without ever touching one.
/// </summary>
public sealed class CapabilityAliasTests : IDisposable
{
    private static readonly ClusterBases Bases = new("example.com", "nodes", "anchors", "play");
    private const string Self = "dns-anchor";
    private const string SelfHost = "home.ddns.net";

    private readonly string _registryPath =
        Path.Combine(Path.GetTempPath(), $"kgsm-dns-tests-{Guid.NewGuid():N}", "names.db");

    private readonly FakeProvider _provider = new();
    private readonly SqliteNameRegistry _registry;
    private readonly RecordReconciler _reconciler;
    private readonly NameService _names;
    private readonly CapabilityNames _capabilities;

    public CapabilityAliasTests()
    {
        _registry = new SqliteNameRegistry(_registryPath);
        _reconciler = Reconciler(_registry);
        _names = new NameService(
            _registry, Bases, new MemberNamePool(["walter"], new Dictionary<string, string>()),
            NullLogger<NameService>.Instance);
        _capabilities = new CapabilityNames(_names, _registry, UnreadState());
    }

    private RecordReconciler Reconciler(SqliteNameRegistry registry) =>
        new(registry, _provider, Bases, ttlSeconds: 60, NullLogger<RecordReconciler>.Instance);

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

    private static ClusterAssignment Unheld(string capability) =>
        new(capability, MemberId: "", Version: 2, SetBy: Self);

    private IReadOnlySet<string> Sync(params ClusterAssignment[] assignments) =>
        _capabilities.Sync(assignments, Self, SelfHost);

    private static string Top(string label) => Bases.Compose(NameKind.CapabilityAlias, label);

    [Fact]
    public async Task An_alias_resolves_to_the_holder_itself_and_names_no_base()
    {
        _registry.StateHost("assistant-anchor", "friend.ddns.net");
        Sync(Held("assistant", "assistant-anchor"));

        NameRow? alias = _names.ClaimCapabilityAlias(Top("assistant"), "assistant");
        await _reconciler.RunAsync(CancellationToken.None);

        Assert.Equal("assistant.example.com", alias!.Name);
        Assert.Equal("assistant-anchor", alias.Member);
        ProviderRecord record = _provider.Find("assistant.example.com")!;
        Assert.Equal("CNAME", record.Type);
        Assert.Equal("friend.ddns.net", record.Content);
        Assert.Equal(new RecordMark(NameKind.CapabilityAlias, "assistant-anchor", "assistant"), record.Mark);
    }

    [Fact]
    public async Task An_alias_follows_its_capability_to_a_new_holder_and_tells_both()
    {
        _registry.StateHost("first", "first.ddns.net");
        _registry.StateHost("second", "second.ddns.net");
        Sync(Held("assistant", "first"));
        _names.ClaimCapabilityAlias(Top("assistant"), "assistant");
        await _reconciler.RunAsync(CancellationToken.None);

        IReadOnlySet<string> touched = Sync(Held("assistant", "second"));
        await _reconciler.RunAsync(CancellationToken.None);

        Assert.Contains("first", touched);
        Assert.Contains("second", touched);
        Assert.Equal("second", _registry.Find(Top("assistant"))!.Member);
        Assert.Equal("second.ddns.net", _provider.Find(Top("assistant"))!.Content);
    }

    [Fact]
    public async Task A_capability_nobody_holds_withdraws_its_alias_and_keeps_it_for_the_next_holder()
    {
        _registry.StateHost("first", "first.ddns.net");
        _registry.StateHost("second", "second.ddns.net");
        Sync(Held("assistant", "first"));
        _names.ClaimCapabilityAlias(Top("assistant"), "assistant");
        await _reconciler.RunAsync(CancellationToken.None);

        Sync(Unheld("assistant"));
        await _reconciler.RunAsync(CancellationToken.None);

        Assert.Null(_provider.Find(Top("assistant")));
        Assert.Equal(NameState.Blocked, _registry.Find(Top("assistant"))!.State);

        Sync(Held("assistant", "second"));
        await _reconciler.RunAsync(CancellationToken.None);

        Assert.Equal("second.ddns.net", _provider.Find(Top("assistant"))!.Content);
    }

    [Fact]
    public async Task A_departed_holder_releases_its_names_but_not_the_alias()
    {
        _registry.StateHost("first", "first.ddns.net");
        Sync(Held("assistant", "first"));
        _names.ClaimCapabilityAlias(Top("assistant"), "assistant");
        await _reconciler.RunAsync(CancellationToken.None);

        IReadOnlyList<string> released = _names.ReleaseAll("first");

        Assert.Contains(Bases.Compose(NameKind.Capability, "assistant"), released);
        Assert.DoesNotContain(Top("assistant"), released);
        Assert.NotNull(_registry.Find(Top("assistant")));
    }

    [Fact]
    public async Task A_hand_made_record_at_the_top_of_the_zone_is_contested_and_never_touched()
    {
        _provider.Seed("kgsm.example.com", "CNAME", "panel.ddns.net");
        _registry.StateHost("first", "first.ddns.net");
        Sync(Held("assistant", "first"));

        _names.ClaimCapabilityAlias(Top("kgsm"), "assistant");
        RecordReconciler.Pass pass = await _reconciler.RunAsync(CancellationToken.None);

        Assert.Contains(Top("kgsm"), pass.Contested);
        Assert.Equal(NameState.Blocked, _registry.Find(Top("kgsm"))!.State);
        Assert.Equal("panel.ddns.net", _provider.Find("kgsm.example.com")!.Content);
    }

    [Fact]
    public async Task A_released_alias_is_withdrawn_and_hand_made_neighbours_stay()
    {
        _provider.Seed("kgsm.example.com", "CNAME", "panel.ddns.net");
        _registry.StateHost("first", "first.ddns.net");
        Sync(Held("assistant", "first"));
        _names.ClaimCapabilityAlias(Top("assistant"), "assistant");
        await _reconciler.RunAsync(CancellationToken.None);

        _names.Release(Top("assistant"));
        await _reconciler.RunAsync(CancellationToken.None);

        Assert.Null(_provider.Find(Top("assistant")));
        Assert.NotNull(_provider.Find("kgsm.example.com"));
    }

    [Fact]
    public async Task An_empty_registry_recovers_the_alias_from_its_mark()
    {
        _registry.StateHost("first", "first.ddns.net");
        Sync(Held("assistant", "first"));
        _names.ClaimCapabilityAlias(Top("assistant"), "assistant");
        await _reconciler.RunAsync(CancellationToken.None);

        string otherPath = Path.Combine(Path.GetDirectoryName(_registryPath)!, "fresh.db");
        var fresh = new SqliteNameRegistry(otherPath);
        await Reconciler(fresh).EnsureReadyAsync(CancellationToken.None);

        NameRow recovered = fresh.Find(Top("assistant"))!;
        Assert.Equal(NameKind.CapabilityAlias, recovered.Kind);
        Assert.Equal("assistant", recovered.Key);
    }

    [Fact]
    public void The_top_of_the_zone_is_not_a_base()
    {
        Assert.False(Bases.Owns("kgsm.example.com"));
        Assert.True(Bases.HoldsAlias("kgsm.example.com"));
        Assert.False(Bases.HoldsAlias("nodes.example.com"));
        Assert.False(Bases.HoldsAlias("walter.nodes.example.com"));
        Assert.False(Bases.HoldsAlias("example.com"));
    }

    [Fact]
    public void A_host_at_the_top_of_the_zone_can_still_be_where_a_member_is_reached()
    {
        Assert.True(PublicHost.TryRead("hotbox.example.com", Bases, out PublicHost host, out _));
        Assert.Equal("hotbox.example.com", host.Content);
    }

    [Fact]
    public void An_alias_takes_a_certificate_and_a_game_alias_does_not()
    {
        Assert.True(NameKinds.TakesCertificate(NameKind.CapabilityAlias));
        Assert.True(NameKinds.TakesCertificate("capability-alias"));
        Assert.False(NameKinds.TakesCertificate(NameKind.Alias));
        Assert.Equal(NameKind.CapabilityAlias, NameKinds.Parse(NameKinds.Spell(NameKind.CapabilityAlias)));
    }
}
