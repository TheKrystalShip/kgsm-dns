using TheKrystalShip.KGSM.Dns;

namespace TheKrystalShip.KGSM.Dns.Tests;

public sealed class DnsLabelTests
{
    [Theory]
    [InlineData("minecraft", "minecraft")]
    [InlineData("Minecraft", "minecraft")]
    [InlineData("7 Days To Die", "7-days-to-die")]
    [InlineData("counter_strike__2", "counter-strike-2")]
    [InlineData("--trimmed--", "trimmed")]
    [InlineData("  ", "")]
    [InlineData(null, "")]
    public void Sanitize_reduces_a_suggestion_to_a_label(string? suggestion, string expected) =>
        Assert.Equal(expected, DnsLabel.Sanitize(suggestion));

    [Fact]
    public void Sanitize_never_exceeds_the_label_limit()
    {
        string label = DnsLabel.Sanitize(new string('a', 200));

        Assert.Equal(DnsLabel.MaxLength, label.Length);
        Assert.True(DnsLabel.IsValid(label));
    }

    [Fact]
    public void A_suggestion_with_nothing_usable_in_it_is_no_label()
    {
        // The caller reports "this cannot be a name" rather than publishing something invented from
        // punctuation, which the provider would refuse later where nobody is reading.
        Assert.Equal("", DnsLabel.Sanitize("!!!"));
        Assert.False(DnsLabel.IsValid(""));
    }

    [Fact]
    public void FirstFree_numbers_from_two_upwards()
    {
        var held = new HashSet<string> { "minecraft", "minecraft-2" };

        Assert.Equal("minecraft-3", DnsLabel.FirstFree("minecraft", held.Contains));
    }

    [Fact]
    public void FirstFree_returns_the_plain_label_when_it_is_free()
    {
        // A name released by an uninstall is free again, so the next claim for that game gets the
        // address players already know.
        Assert.Equal("factorio", DnsLabel.FirstFree("factorio", _ => false));
    }

    [Fact]
    public void FirstFree_keeps_a_suffixed_label_inside_the_limit()
    {
        string label = new('a', DnsLabel.MaxLength);

        string free = DnsLabel.FirstFree(label, candidate => candidate == label);

        Assert.True(free.Length <= DnsLabel.MaxLength);
        Assert.True(DnsLabel.IsValid(free));
        Assert.EndsWith("-2", free, StringComparison.Ordinal);
    }
}

public sealed class ClusterBasesTests
{
    private static readonly ClusterBases Bases = new("example.com", "nodes", "anchors", "play");

    [Fact]
    public void Each_kind_composes_under_its_own_base()
    {
        Assert.Equal("jessie.nodes.example.com", Bases.Compose(NameKind.Member, "jessie"));
        Assert.Equal("assistant.anchors.example.com", Bases.Compose(NameKind.Capability, "assistant"));
        Assert.Equal("minecraft.play.example.com", Bases.Compose(NameKind.Game, "minecraft"));
        Assert.Equal("smp.play.example.com", Bases.Compose(NameKind.Alias, "smp"));
    }

    [Fact]
    public void An_alias_lives_in_the_play_base_beside_the_games() =>
        Assert.True(Bases.Owns(Bases.Compose(NameKind.Alias, "smp")));

    [Fact]
    public void A_name_outside_every_base_is_not_the_clusters()
    {
        // The names that matter most to protect are the ones people sign in through, and they sit at
        // the top of the zone where nothing this anchor writes can reach.
        Assert.False(Bases.Owns("auth.example.com"));
        Assert.False(Bases.Owns("example.com"));
        Assert.False(Bases.Owns("nodes.example.com"));
        Assert.False(Bases.Owns("jessie.nodes.other.com"));
    }

    [Fact]
    public void A_name_deeper_than_one_label_under_a_base_is_not_the_clusters()
    {
        // Nothing publishes such a name, so a record carrying one was made by something else.
        Assert.False(Bases.Owns("a.b.nodes.example.com"));
        Assert.Null(Bases.LabelOf(NameKind.Member, "a.b.nodes.example.com"));
    }

    [Fact]
    public void A_challenge_name_sits_outside_the_base_it_validates_under() =>
        Assert.False(Bases.Owns(ClusterBases.ChallengeFor("jessie.nodes.example.com")));

    [Fact]
    public void LabelOf_reads_the_label_back() =>
        Assert.Equal("jessie", Bases.LabelOf(NameKind.Member, "jessie.nodes.example.com"));
}

public sealed class NameKindsTests
{
    [Theory]
    [InlineData(NameKind.Member, true)]
    [InlineData(NameKind.Capability, true)]
    [InlineData(NameKind.Game, false)]
    [InlineData(NameKind.Alias, false)]
    public void Only_a_member_or_a_capability_name_ever_takes_a_certificate(NameKind kind, bool takes) =>
        Assert.Equal(takes, NameKinds.TakesCertificate(kind));

    [Fact]
    public void Alias_spells_and_parses_back() =>
        Assert.Equal(NameKind.Alias, NameKinds.Parse(NameKinds.Spell(NameKind.Alias)));
}

public sealed class RecordMarkTests
{
    [Fact]
    public void A_mark_round_trips()
    {
        var mark = new RecordMark(NameKind.Game, "jessie", "factorio-01");

        RecordMark? read = RecordMark.Parse(mark.ToString());

        Assert.Equal(mark, read);
    }

    [Fact]
    public void An_alias_mark_round_trips()
    {
        var mark = new RecordMark(NameKind.Alias, "jessie", "factorio-01");

        RecordMark? read = RecordMark.Parse(mark.ToString());

        Assert.Equal(mark, read);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("managed by hand, do not touch")]
    [InlineData("kgsm:game:jessie")]
    [InlineData("kgsm:unknown:jessie:factorio")]
    [InlineData("other:game:jessie:factorio")]
    public void Anything_else_is_not_a_mark(string? comment) => Assert.Null(RecordMark.Parse(comment));

    [Fact]
    public void A_members_own_name_carries_an_empty_key()
    {
        var mark = new RecordMark(NameKind.Member, "jessie", "");

        Assert.Equal("kgsm:member:jessie:", mark.ToString());
        Assert.Equal(mark, RecordMark.Parse(mark.ToString()));
    }
}

public sealed class PublicHostTests
{
    private static readonly ClusterBases Bases = new("example.com", "nodes", "anchors", "play");

    [Theory]
    [InlineData("hotbox.ddns.net", "CNAME", "hotbox.ddns.net")]
    [InlineData("Hotbox.DDNS.net.", "CNAME", "hotbox.ddns.net")]
    [InlineData("95.19.50.122", "A", "95.19.50.122")]
    [InlineData("2001:4860:4860::8888", "AAAA", "2001:4860:4860::8888")]
    public void A_public_host_becomes_the_record_that_reaches_it(string written, string type, string content)
    {
        Assert.True(PublicHost.TryRead(written, Bases, out PublicHost host, out _));
        Assert.Equal(type, host.Type);
        Assert.Equal(content, host.Content);
    }

    [Theory]
    [InlineData("")]
    [InlineData("10.0.0.4")]
    [InlineData("172.20.1.1")]
    [InlineData("192.168.1.128")]
    [InlineData("100.64.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.1.1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    [InlineData("localhost")]
    [InlineData("under_score.example.org")]
    [InlineData("walter.nodes.example.com")]
    [InlineData("play.example.com")]
    public void A_host_nobody_outside_could_reach_is_refused_with_a_reason(string written)
    {
        Assert.False(PublicHost.TryRead(written, Bases, out _, out string reason));
        Assert.NotEmpty(reason);
    }
}
