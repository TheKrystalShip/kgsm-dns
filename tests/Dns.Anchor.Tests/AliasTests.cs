using Microsoft.Extensions.Logging.Abstractions;

using TheKrystalShip.KGSM.Dns;
using TheKrystalShip.KGSM.Dns.Anchor.Names;
using TheKrystalShip.KGSM.Dns.Anchor.Providers;
using TheKrystalShip.KGSM.Dns.Anchor.Registry;

namespace TheKrystalShip.KGSM.Dns.Anchor.Tests;

/// <summary>
/// An alias names an existing game server under an additional label. It is claimed at the same node and
/// instance the game name is, so every rule that follows a game name — retargeting on the node, blocking
/// when the node cannot be, release with the server — applies to it unchanged.
/// </summary>
public sealed class AliasTests : IDisposable
{
    private static readonly ClusterBases Bases = new("example.com", "nodes", "anchors", "play");

    private readonly string _registryPath =
        Path.Combine(Path.GetTempPath(), $"kgsm-dns-tests-{Guid.NewGuid():N}", "names.db");

    private readonly FakeProvider _provider = new();
    private readonly SqliteNameRegistry _registry;
    private readonly RecordReconciler _reconciler;
    private readonly NameService _names;

    public AliasTests()
    {
        _registry = new SqliteNameRegistry(_registryPath);
        _reconciler = new RecordReconciler(
            _registry, _provider, Bases, ttlSeconds: 60, NullLogger<RecordReconciler>.Instance);
        _names = new NameService(
            _registry, Bases, new MemberNamePool(["walter"], new Dictionary<string, string>()),
            NullLogger<NameService>.Instance);
    }

    public void Dispose()
    {
        string? directory = Path.GetDirectoryName(_registryPath);
        if (directory is not null && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private static string Play(string label) => Bases.Compose(NameKind.Game, label);

    private static string Alias(string label) => Bases.Compose(NameKind.Alias, label);

    private static string Node(string label) => Bases.Compose(NameKind.Member, label);

    [Fact]
    public async Task An_alias_points_at_the_same_node_as_its_server()
    {
        _names.ClaimMember("node-a", "home.ddns.net");
        NameRow game = _names.ClaimGame("node-a", "factorio-1", "factorio");

        NameRow? alias = _names.ClaimAlias(Alias("smp"), game.Member, game.Key);
        await _reconciler.RunAsync(CancellationToken.None);

        Assert.NotNull(alias);
        Assert.Equal(NameKind.Alias, alias!.Kind);
        ProviderRecord record = _provider.Find(alias.Name)!;
        Assert.Equal("CNAME", record.Type);
        Assert.Equal(Node("walter"), record.Content);
        Assert.Equal(new RecordMark(NameKind.Alias, "node-a", "factorio-1"), record.Mark);
    }

    [Fact]
    public void A_name_already_held_refuses_a_second_alias()
    {
        _names.ClaimMember("node-a", "home.ddns.net");
        NameRow game = _names.ClaimGame("node-a", "factorio-1", "factorio");
        Assert.NotNull(_names.ClaimAlias(Alias("smp"), game.Member, game.Key));

        Assert.Null(_names.ClaimAlias(Alias("smp"), game.Member, game.Key));
    }

    [Fact]
    public async Task An_alias_follows_its_node_once_the_node_is_named()
    {
        NameRow game = _names.ClaimGame("node-a", "factorio-1", "factorio");
        _names.ClaimAlias(Alias("smp"), game.Member, game.Key);

        _names.ClaimMember("node-a", "home.ddns.net");
        IReadOnlySet<string> touched = _names.RetargetGames();
        await _reconciler.RunAsync(CancellationToken.None);

        Assert.Equal(["node-a"], touched);
        Assert.Equal(Node("walter"), _provider.Find(Alias("smp"))!.Content);
    }

    [Fact]
    public async Task A_node_whose_name_is_blocked_withdraws_its_aliases_too()
    {
        _names.ClaimMember("node-a", "home.ddns.net");
        NameRow game = _names.ClaimGame("node-a", "factorio-1", "factorio");
        _names.ClaimAlias(Alias("smp"), game.Member, game.Key);
        await _reconciler.RunAsync(CancellationToken.None);

        _names.ClaimMember("node-a", "192.168.1.10");
        _names.RetargetGames();
        await _reconciler.RunAsync(CancellationToken.None);

        Assert.Equal(NameState.Blocked, _registry.Find(Alias("smp"))!.State);
        Assert.Null(_provider.Find(Alias("smp")));
    }

    [Fact]
    public async Task An_alias_is_released_when_its_server_is_uninstalled()
    {
        _names.ClaimMember("node-a", "home.ddns.net");
        NameRow game = _names.ClaimGame("node-a", "factorio-1", "factorio");
        _names.ClaimAlias(Alias("smp"), game.Member, game.Key);
        await _reconciler.RunAsync(CancellationToken.None);
        Assert.NotNull(_provider.Find(Alias("smp")));

        IReadOnlyList<string> released = _names.ReleaseGamesExcept("node-a", new HashSet<string>());
        await _reconciler.RunAsync(CancellationToken.None);

        Assert.Contains(Play("factorio"), released);
        Assert.Contains(Alias("smp"), released);
        Assert.Null(_provider.Find(Play("factorio")));
        Assert.Null(_provider.Find(Alias("smp")));
    }

    [Fact]
    public async Task An_alias_is_released_when_its_node_leaves_the_cluster()
    {
        _names.ClaimMember("node-a", "home.ddns.net");
        NameRow game = _names.ClaimGame("node-a", "factorio-1", "factorio");
        _names.ClaimAlias(Alias("smp"), game.Member, game.Key);
        await _reconciler.RunAsync(CancellationToken.None);

        IReadOnlyList<string> released = _names.ReleaseAll("node-a");
        await _reconciler.RunAsync(CancellationToken.None);

        Assert.Contains(Alias("smp"), released);
        Assert.Empty(_registry.ForMember("node-a"));
    }

    [Fact]
    public void Giving_up_one_alias_leaves_its_server_and_other_aliases_alone()
    {
        _names.ClaimMember("node-a", "home.ddns.net");
        NameRow game = _names.ClaimGame("node-a", "factorio-1", "factorio");
        _names.ClaimAlias(Alias("smp"), game.Member, game.Key);
        _names.ClaimAlias(Alias("creative"), game.Member, game.Key);

        _names.Release(Alias("smp"));

        Assert.Null(_registry.Find(Alias("smp")));
        Assert.NotNull(_registry.Find(Alias("creative")));
        Assert.NotNull(_registry.Find(Play("factorio")));
    }

    [Fact]
    public async Task An_alias_is_rebuilt_from_its_mark()
    {
        _names.ClaimMember("node-a", "home.ddns.net");
        NameRow game = _names.ClaimGame("node-a", "factorio-1", "factorio");
        _names.ClaimAlias(Alias("smp"), game.Member, game.Key);
        await _reconciler.RunAsync(CancellationToken.None);

        var rebuilt = new SqliteNameRegistry(Path.Combine(Path.GetDirectoryName(_registryPath)!, "rebuilt.db"));
        var reconciler = new RecordReconciler(
            rebuilt, _provider, Bases, ttlSeconds: 60, NullLogger<RecordReconciler>.Instance);
        await reconciler.EnsureReadyAsync(CancellationToken.None);

        NameRow? recovered = rebuilt.Find(Alias("smp"));
        Assert.NotNull(recovered);
        Assert.Equal(NameKind.Alias, recovered!.Kind);
        Assert.Equal("node-a", recovered.Member);
        Assert.Equal("factorio-1", recovered.Key);
    }
}
