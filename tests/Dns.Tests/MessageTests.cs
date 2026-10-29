using System.Text.Json;
using System.Text.Json.Serialization;

using TheKrystalShip.KGSM.Dns.Member;
using TheKrystalShip.KGSM.Dns.Messages;

namespace TheKrystalShip.KGSM.Dns.Tests;

public sealed partial class MessageTests
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
    public void A_game_server_list_round_trips_with_its_ports()
    {
        var sent = new DnsInstances([new DnsInstance("factorio-1", "factorio", [new DnsPort(34197, 34197, "udp")])]);

        string json = JsonSerializer.Serialize(sent, DnsJsonContext.Default.DnsInstances);
        using JsonDocument document = JsonDocument.Parse(json);
        DnsInstances read = DnsJsonContext.Read(document.RootElement, DnsJsonContext.Default.DnsInstances)!;

        Assert.Contains("\"instances\":[{\"instance\":\"factorio-1\"", json);
        Assert.Equal(34197, read.Instances[0].Ports[0].Start);
        Assert.Equal("udp", read.Instances[0].Ports[0].Protocol);
    }

    [Fact]
    public void An_inheritance_round_trips_and_is_omitted_from_the_wire_when_absent()
    {
        var inherited = new DnsInstance(
            "factorio-9", "factorio", [], new DnsInheritance("node-a", "factorio-1", "factorio.play.example.com"));
        var plain = new DnsInstance("factorio-1", "factorio", []);

        string withJson = JsonSerializer.Serialize(new DnsInstances([inherited]), DnsJsonContext.Default.DnsInstances);
        string withoutJson = JsonSerializer.Serialize(new DnsInstances([plain]), DnsJsonContext.Default.DnsInstances);
        DnsInstances read = JsonSerializer.Deserialize(withJson, DnsJsonContext.Default.DnsInstances)!;

        Assert.Contains("\"inherits\":{\"member\":\"node-a\",\"instance\":\"factorio-1\"", withJson);
        Assert.DoesNotContain("inherits", withoutJson);
        Assert.Equal(inherited.Inherits, read.Instances[0].Inherits);
    }

    [Fact]
    public void A_message_without_an_inheritance_reads_with_none()
    {
        DnsInstances read = JsonSerializer.Deserialize(
            "{\"instances\":[{\"instance\":\"factorio-1\",\"blueprint\":\"factorio\",\"ports\":[]}]}",
            DnsJsonContext.Default.DnsInstances)!;

        Assert.Null(read.Instances[0].Inherits);
    }

    [Fact]
    public void A_reader_of_the_old_shape_ignores_the_inheritance_it_does_not_know()
    {
        // An anchor that predates the field reads the same message through a context with the same
        // options and no such member; the unknown property is skipped, not refused.
        var sent = new DnsInstances([new DnsInstance(
            "factorio-9", "factorio", [], new DnsInheritance("node-a", "factorio-1", null))]);
        string json = JsonSerializer.Serialize(sent, DnsJsonContext.Default.DnsInstances);

        OldInstances? read = JsonSerializer.Deserialize(json, OldShapeContext.Default.OldInstances);

        Assert.Equal("factorio-9", read!.Instances[0].Instance);
    }

    private sealed record OldInstance(string Instance, string Blueprint);

    private sealed record OldInstances(IReadOnlyList<OldInstance> Instances);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(OldInstances))]
    private sealed partial class OldShapeContext : JsonSerializerContext;

    [Fact]
    public void A_game_server_is_reached_at_its_name_only_once_published_and_is_never_served()
    {
        var book = new DnsNameBook();

        book.Replace([new DnsNameEntry("factorio.play.example.com", "game", "factorio-1", "pending", "")]);
        Assert.Null(book.GameName("factorio-1"));

        book.Replace([new DnsNameEntry("factorio.play.example.com", "game", "factorio-1", "published", "")]);
        Assert.Equal("factorio.play.example.com", book.GameName("factorio-1"));
        Assert.Null(book.GameName("factorio-2"));
        Assert.Empty(book.Published);
    }

    [Fact]
    public void A_game_names_collisions_ride_the_names_and_absence_reads_as_unknown()
    {
        var sent = new DnsNames([
            new DnsNameEntry("minecraft.play.example.com", "game", "mc-1", "published", "",
                [new DnsCollision("minecraft-2.play.example.com", "node-b", 25565, 25565, "tcp")]),
        ]);
        string json = JsonSerializer.Serialize(sent, DnsJsonContext.Default.DnsNames);

        var book = new DnsNameBook();
        using (JsonDocument document = JsonDocument.Parse(json))
            book.Replace(DnsJsonContext.Read(document.RootElement, DnsJsonContext.Default.DnsNames)!.Names);
        Assert.Equal("node-b", Assert.Single(book.GameCollisions("mc-1")!).Member);

        // A payload from an anchor that says nothing about collisions has not said there are none.
        using JsonDocument older = JsonDocument.Parse(
            "{\"names\":[{\"name\":\"minecraft.play.example.com\",\"kind\":\"game\",\"key\":\"mc-1\",\"state\":\"published\",\"note\":\"\"}]}");
        book.Replace(DnsJsonContext.Read(older.RootElement, DnsJsonContext.Default.DnsNames)!.Names);
        Assert.Null(book.GameCollisions("mc-1"));
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
