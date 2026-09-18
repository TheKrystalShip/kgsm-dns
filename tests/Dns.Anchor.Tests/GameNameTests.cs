using Microsoft.Extensions.Logging.Abstractions;

using TheKrystalShip.KGSM.Dns;
using TheKrystalShip.KGSM.Dns.Anchor.Names;
using TheKrystalShip.KGSM.Dns.Anchor.Providers;
using TheKrystalShip.KGSM.Dns.Anchor.Registry;

namespace TheKrystalShip.KGSM.Dns.Anchor.Tests;

public sealed class GameNameTests : IDisposable
{
    private static readonly ClusterBases Bases = new("example.com", "nodes", "anchors", "play");

    private readonly string _registryPath =
        Path.Combine(Path.GetTempPath(), $"kgsm-dns-tests-{Guid.NewGuid():N}", "names.db");

    private readonly FakeProvider _provider = new();
    private readonly SqliteNameRegistry _registry;
    private readonly RecordReconciler _reconciler;
    private readonly NameService _names;

    public GameNameTests()
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
    }

    public void Dispose()
    {
        string? directory = Path.GetDirectoryName(_registryPath);
        if (directory is not null && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private static string Play(string label) => Bases.Compose(NameKind.Game, label);

    private static string Node(string label) => Bases.Compose(NameKind.Member, label);

    [Fact]
    public async Task A_game_server_is_an_alias_of_its_nodes_own_name()
    {
        _names.ClaimMember("node-a", "home.ddns.net");
        NameRow game = _names.ClaimGame("node-a", "factorio-1", "factorio");
        await _reconciler.RunAsync(CancellationToken.None);

        Assert.Equal(Play("factorio"), game.Name);
        ProviderRecord record = _provider.Find(game.Name)!;
        Assert.Equal("CNAME", record.Type);
        Assert.Equal(Node("walter"), record.Content);
        Assert.Equal(new RecordMark(NameKind.Game, "node-a", "factorio-1"), record.Mark);
    }

    [Fact]
    public void A_second_server_of_the_same_game_takes_the_lowest_free_suffix_on_any_node()
    {
        _names.ClaimMember("node-a", "home.ddns.net");
        _names.ClaimMember("node-b", "friend.ddns.net");

        NameRow first = _names.ClaimGame("node-a", "factorio-1", "factorio");
        NameRow second = _names.ClaimGame("node-b", "space-age", "factorio");
        NameRow third = _names.ClaimGame("node-a", "factorio-2", "factorio");

        Assert.Equal(Play("factorio"), first.Name);
        Assert.Equal(Play("factorio-2"), second.Name);
        Assert.Equal(Play("factorio-3"), third.Name);
    }

    [Fact]
    public void A_node_restating_its_servers_keeps_every_name_it_has()
    {
        _names.ClaimMember("node-a", "home.ddns.net");
        NameRow first = _names.ClaimGame("node-a", "factorio-1", "factorio");

        NameRow again = _names.ClaimGame("node-a", "factorio-1", "factorio");

        Assert.Equal(first.Name, again.Name);
        Assert.Single(_registry.OfKind(NameKind.Game));
    }

    [Fact]
    public async Task An_uninstalled_server_loses_its_name_and_only_that_node_is_affected()
    {
        _names.ClaimMember("node-a", "home.ddns.net");
        _names.ClaimMember("node-b", "friend.ddns.net");
        _names.ClaimGame("node-a", "factorio-1", "factorio");
        _names.ClaimGame("node-a", "terraria-1", "terraria");
        _names.ClaimGame("node-b", "factorio-b", "factorio");
        await _reconciler.RunAsync(CancellationToken.None);

        IReadOnlyList<string> released = _names.ReleaseGamesExcept("node-a", new HashSet<string> { "terraria-1" });
        await _reconciler.RunAsync(CancellationToken.None);

        Assert.Equal([Play("factorio")], released);
        Assert.Null(_provider.Find(Play("factorio")));
        Assert.NotNull(_provider.Find(Play("terraria")));
        Assert.NotNull(_provider.Find(Play("factorio-2")));
    }

    [Fact]
    public void A_released_label_is_free_again_for_the_next_install()
    {
        _names.ClaimMember("node-a", "home.ddns.net");
        _names.ClaimGame("node-a", "factorio-1", "factorio");
        _names.ReleaseGamesExcept("node-a", new HashSet<string>());

        NameRow reinstalled = _names.ClaimGame("node-a", "factorio-7", "factorio");

        Assert.Equal(Play("factorio"), reinstalled.Name);
    }

    [Fact]
    public async Task A_node_without_a_name_has_game_names_that_publish_nothing()
    {
        NameRow game = _names.ClaimGame("node-a", "factorio-1", "factorio");
        await _reconciler.RunAsync(CancellationToken.None);

        Assert.Equal(NameState.Blocked, game.State);
        Assert.NotEmpty(game.Note);
        Assert.Null(_provider.Find(game.Name));
    }

    [Fact]
    public async Task A_game_name_follows_its_node_once_the_node_is_named()
    {
        _names.ClaimGame("node-a", "factorio-1", "factorio");
        _names.ClaimMember("node-a", "home.ddns.net");

        IReadOnlySet<string> touched = _names.RetargetGames();
        await _reconciler.RunAsync(CancellationToken.None);

        Assert.Equal(["node-a"], touched);
        Assert.Equal(Node("walter"), _provider.Find(Play("factorio"))!.Content);
        Assert.Empty(_names.RetargetGames());
    }

    [Fact]
    public async Task A_node_whose_name_is_blocked_withdraws_its_game_names()
    {
        _names.ClaimMember("node-a", "home.ddns.net");
        _names.ClaimGame("node-a", "factorio-1", "factorio");
        await _reconciler.RunAsync(CancellationToken.None);

        _names.ClaimMember("node-a", "192.168.1.10");
        _names.RetargetGames();
        await _reconciler.RunAsync(CancellationToken.None);

        Assert.Equal(NameState.Blocked, _registry.Find(Play("factorio"))!.State);
        Assert.Null(_provider.Find(Play("factorio")));
    }

    [Fact]
    public async Task A_node_host_change_moves_no_game_record()
    {
        _names.ClaimMember("node-a", "home.ddns.net");
        _names.ClaimGame("node-a", "factorio-1", "factorio");
        await _reconciler.RunAsync(CancellationToken.None);

        _names.ClaimMember("node-a", "elsewhere.ddns.net");

        Assert.Empty(_names.RetargetGames());
        RecordReconciler.Pass pass = await _reconciler.RunAsync(CancellationToken.None);
        Assert.Equal(1, pass.Moved);
        Assert.Equal(Node("walter"), _provider.Find(Play("factorio"))!.Content);
    }

    [Fact]
    public async Task Game_names_are_rebuilt_from_their_marks()
    {
        _names.ClaimMember("node-a", "home.ddns.net");
        _names.ClaimGame("node-a", "factorio-1", "factorio");
        await _reconciler.RunAsync(CancellationToken.None);

        var rebuilt = new SqliteNameRegistry(Path.Combine(Path.GetDirectoryName(_registryPath)!, "rebuilt.db"));
        var reconciler = new RecordReconciler(
            rebuilt, _provider, Bases, ttlSeconds: 60, NullLogger<RecordReconciler>.Instance);
        await reconciler.EnsureReadyAsync(CancellationToken.None);

        NameRow? game = rebuilt.FindFor(NameKind.Game, "node-a", "factorio-1");
        Assert.NotNull(game);
        Assert.Equal(Play("factorio"), game.Name);
    }

    [Fact]
    public void A_game_the_label_cannot_spell_is_blocked_rather_than_invented()
    {
        _names.ClaimMember("node-a", "home.ddns.net");

        NameRow game = _names.ClaimGame("node-a", "odd-1", "---");

        Assert.Equal(NameState.Blocked, game.State);
    }
}
