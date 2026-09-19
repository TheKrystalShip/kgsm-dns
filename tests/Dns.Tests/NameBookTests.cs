using TheKrystalShip.KGSM.Dns.Member;
using TheKrystalShip.KGSM.Dns.Messages;

namespace TheKrystalShip.KGSM.Dns.Tests;

public sealed class NameBookTests
{
    [Fact]
    public void A_name_waiting_for_its_record_is_still_served_and_a_blocked_one_is_not()
    {
        // The anchor answers a member that has just stated itself before the zone pass writes the record.
        // Reading that answer as "no names" would withdraw the member's site until the next answer — which
        // may not reach it for as long as a resolver remembers the name was missing.
        var book = new DnsNameBook();
        book.Replace([
            new DnsNameEntry("jessie.nodes.example.com", NameKinds.Spell(NameKind.Member), "", "pending", ""),
            new DnsNameEntry("bot.anchors.example.com", NameKinds.Spell(NameKind.Capability), "bot", "blocked", "no host"),
            new DnsNameEntry("ark.play.example.com", NameKinds.Spell(NameKind.Game), "ark-1", "published", ""),
        ]);

        Assert.Equal(["jessie.nodes.example.com"], book.Held);

        // A certificate is asked for only once the name resolves: the anchor refuses one before that.
        Assert.Empty(book.Published);
    }
}
