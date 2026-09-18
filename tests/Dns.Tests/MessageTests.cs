using System.Text.Json;

using TheKrystalShip.KGSM.Dns.Member;
using TheKrystalShip.KGSM.Dns.Messages;

namespace TheKrystalShip.KGSM.Dns.Tests;

public sealed class MessageTests
{
    [Fact]
    public void A_statement_round_trips_in_camel_case()
    {
        string json = JsonSerializer.Serialize(new DnsSelf("hotbox.ddns.net"), DnsJsonContext.Default.DnsSelf);

        Assert.Equal("{\"publicHost\":\"hotbox.ddns.net\"}", json);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal("hotbox.ddns.net", DnsJsonContext.Read(document.RootElement, DnsJsonContext.Default.DnsSelf)!.PublicHost);
    }

    [Fact]
    public void A_payload_of_another_shape_reads_as_nothing_rather_than_throwing()
    {
        // A handler that threw would have the sender retry forever against a payload that can never
        // become valid.
        using JsonDocument document = JsonDocument.Parse("[1,2,3]");

        Assert.Null(DnsJsonContext.Read(document.RootElement, DnsJsonContext.Default.DnsNames));
    }

    [Fact]
    public void Not_yet_told_and_told_nothing_are_different_answers()
    {
        // A member that has not heard from the anchor since it started keeps serving what it served; one
        // the anchor told it holds nothing withdraws it.
        var book = new DnsNameBook();
        Assert.False(book.Answered);

        book.Replace([]);

        Assert.True(book.Answered);
        Assert.Empty(book.Published);
    }

    [Fact]
    public void A_capability_name_is_served_once_published_and_is_no_node_name()
    {
        var book = new DnsNameBook();

        book.Replace([new DnsNameEntry("auth.anchors.example.com", "capability", "auth", "published", "")]);

        Assert.Equal(["auth.anchors.example.com"], book.Published);
        Assert.Null(book.NodeName);
    }

    [Fact]
    public void A_node_is_reached_at_its_name_only_once_the_provider_carries_it()
    {
        var book = new DnsNameBook();

        book.Replace([new DnsNameEntry("walter.nodes.example.com", "member", "", "pending", "")]);
        Assert.Null(book.NodeName);

        book.Replace([new DnsNameEntry("walter.nodes.example.com", "member", "", "published", "")]);
        Assert.Equal("walter.nodes.example.com", book.NodeName);
    }

    [Fact]
    public void A_capability_name_is_not_the_nodes_own()
    {
        var book = new DnsNameBook();

        book.Replace([new DnsNameEntry("auth.anchors.example.com", "capability", "auth", "published", "")]);

        Assert.Null(book.NodeName);
    }
}
