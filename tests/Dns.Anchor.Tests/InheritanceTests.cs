using Microsoft.Extensions.Logging.Abstractions;

using TheKrystalShip.KGSM.Dns;
using TheKrystalShip.KGSM.Dns.Anchor.Names;
using TheKrystalShip.KGSM.Dns.Anchor.Providers;
using TheKrystalShip.KGSM.Dns.Anchor.Registry;
using TheKrystalShip.KGSM.Dns.Messages;

namespace TheKrystalShip.KGSM.Dns.Anchor.Tests;

/// <summary>A transferred server continues the game name its source held, in whichever order the two nodes report.</summary>
public sealed class InheritanceTests : IDisposable
{
    private static readonly ClusterBases Bases = new("example.com", "nodes", "anchors", "play");

    private readonly string _registryPath =
        Path.Combine(Path.GetTempPath(), $"kgsm-dns-tests-{Guid.NewGuid():N}", "names.db");

    private readonly FakeProvider _provider = new();
    private readonly SqliteNameRegistry _registry;
    private readonly RecordReconciler _reconciler;
    private readonly NameService _names;

    public InheritanceTests()
    {
        _registry = new SqliteNameRegistry(_registryPath);
        _reconciler = new RecordReconciler(
            _registry, _provider, Bases, ttlSeconds: 60, NullLogger<RecordReconciler>.Instance);
        _names = new NameService(
            _registry,
            Bases,
            new MemberNamePool(["walter", "jessie"],
                new Dictionary<string, string> { ["node-a"] = "walter", ["node-b"] = "jessie" }),
            NullLogger<NameService>.Instance);
        _names.ClaimMember("node-a", "home.ddns.net");
        _names.ClaimMember("node-b", "friend.ddns.net");
    }

    public void Dispose()
    {
        string? directory = Path.GetDirectoryName(_registryPath);
        if (directory is not null && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private static string Play(string label) => Bases.Compose(NameKind.Game, label);

    private static DnsInheritance FromSource(string? name = null) => new("node-a", "factorio-1", name);

    [Fact]
    public async Task The_target_reporting_first_takes_the_name_the_source_still_holds()
    {
        _names.ClaimGame("node-a", "factorio-1", "factorio");
        _names.ClaimAlias(Play("space"), "node-a", "factorio-1");
        await _reconciler.RunAsync(CancellationToken.None);
        string recordId = _provider.Find(Play("factorio"))!.Id;

        NameRow row = _names.ClaimGame("node-b", "factorio-7", "factorio", FromSource(Play("factorio")));
        _names.ReleaseGamesExcept("node-a", new HashSet<string>());
        await _reconciler.RunAsync(CancellationToken.None);

        Assert.Equal(Play("factorio"), row.Name);
        Assert.Equal(recordId, _provider.Find(Play("factorio"))!.Id);
        Assert.Equal(new RecordMark(NameKind.Game, "node-b", "factorio-7"), _provider.Find(Play("factorio"))!.Mark);
        Assert.Equal(Bases.Compose(NameKind.Member, "jessie"), _provider.Find(Play("factorio"))!.Content);
        Assert.Equal(new RecordMark(NameKind.Alias, "node-b", "factorio-7"), _provider.Find(Play("space"))!.Mark);
        Assert.DoesNotContain(_registry.ForMember("node-a"), r => r.Kind is NameKind.Game or NameKind.Alias);
    }

    [Fact]
    public async Task The_sources_release_first_leaves_the_name_for_the_target_to_inherit_by_name()
    {
        _names.ClaimGame("node-a", "factorio-1", "factorio");
        _names.ReleaseGamesExcept("node-a", new HashSet<string>());

        NameRow row = _names.ClaimGame("node-b", "factorio-7", "factorio", FromSource(Play("factorio")));
        await _reconciler.RunAsync(CancellationToken.None);

        Assert.Equal(Play("factorio"), row.Name);
        Assert.Equal(new RecordMark(NameKind.Game, "node-b", "factorio-7"), _provider.Find(Play("factorio"))!.Mark);
    }

    [Fact]
    public void A_name_taken_meanwhile_gives_the_target_a_fresh_name()
    {
        _names.ClaimGame("node-a", "factorio-1", "factorio");
        _names.ReleaseGamesExcept("node-a", new HashSet<string>());
        _names.ClaimGame("node-a", "factorio-2", "factorio");

        NameRow row = _names.ClaimGame("node-b", "factorio-7", "factorio", FromSource(Play("factorio")));

        Assert.Equal(Play("factorio-2"), row.Name);
        Assert.Equal("node-a", _registry.Find(Play("factorio"))!.Member);
    }

    [Fact]
    public void An_inheritance_naming_nothing_is_a_fresh_install()
    {
        NameRow row = _names.ClaimGame("node-b", "factorio-7", "factorio", FromSource());

        Assert.Equal(Play("factorio"), row.Name);
    }

    [Fact]
    public void Restating_the_inheritance_changes_nothing()
    {
        _names.ClaimGame("node-a", "factorio-1", "factorio");
        NameRow first = _names.ClaimGame("node-b", "factorio-7", "factorio", FromSource(Play("factorio")));
        _names.ClaimGame("node-a", "terraria-1", "terraria");

        NameRow again = _names.ClaimGame("node-b", "factorio-7", "factorio", FromSource(Play("factorio")));

        Assert.Equal(first, again);
        Assert.Equal(2, _registry.OfKind(NameKind.Game).Count);
    }

    [Fact]
    public void An_instance_that_already_holds_a_name_ignores_the_inheritance()
    {
        NameRow held = _names.ClaimGame("node-b", "factorio-7", "factorio");
        NameRow source = _names.ClaimGame("node-a", "factorio-1", "factorio");

        NameRow again = _names.ClaimGame("node-b", "factorio-7", "factorio", FromSource(source.Name));

        Assert.Equal(held.Name, again.Name);
        Assert.Equal("node-a", _registry.Find(source.Name)!.Member);
    }

    [Fact]
    public void A_message_without_an_inheritance_names_as_it_always_did()
    {
        _names.ClaimGame("node-a", "factorio-1", "factorio");

        NameRow row = _names.ClaimGame("node-b", "factorio-7", "factorio");

        Assert.Equal(Play("factorio-2"), row.Name);
    }

    [Fact]
    public void A_stated_name_outside_the_play_base_is_never_taken()
    {
        NameRow row = _names.ClaimGame(
            "node-b", "factorio-7", "factorio", new DnsInheritance("node-a", "gone", "panel.example.com"));

        Assert.Equal(Play("factorio"), row.Name);
        Assert.Null(_registry.Find("panel.example.com"));
    }
}
