using System.Text.Json;
using System.Text.Json.Serialization;

namespace TheKrystalShip.KGSM.Dns.Messages;

/// <summary>The bus message types this component speaks.</summary>
public static class DnsMessageTypes
{
    /// <summary>
    /// A node to the DNS anchor: where this node is reached from the internet. Sent whole every time,
    /// so the latest one is the whole truth and a lost or repeated one changes nothing.
    /// </summary>
    public const string Self = "dns.self";

    /// <summary>
    /// The DNS anchor to a member: every name the registry holds for that member, and where each
    /// stands. A complete set, for the same reason.
    /// </summary>
    public const string Names = "dns.names";
}

/// <summary>What a node says about itself.</summary>
/// <param name="PublicHost">
/// Where the node is reached from the internet: normally its network's dynamic-DNS name, or a fixed
/// public address. Empty is a real answer — a node that states no host is named but not published.
/// </param>
public sealed record DnsSelf(string PublicHost);

/// <summary>One name a member holds.</summary>
/// <param name="Name">The fully qualified name.</param>
/// <param name="Kind">What it names, spelled as <see cref="NameKinds.Spell"/> spells it.</param>
/// <param name="Key">The capability or instance within the member, empty for a node's own name.</param>
/// <param name="State">`pending`, `published` or `blocked`.</param>
/// <param name="Note">Why a blocked name is blocked. Empty otherwise.</param>
public sealed record DnsNameEntry(string Name, string Kind, string Key, string State, string Note)
{
    /// <summary>Whether the provider holds the record, so the name resolves.</summary>
    [JsonIgnore]
    public bool Published => string.Equals(State, "published", StringComparison.Ordinal);
}

/// <summary>Every name the anchor holds for one member.</summary>
/// <param name="Names">The complete set. An empty list means the member holds no name.</param>
public sealed record DnsNames(IReadOnlyList<DnsNameEntry> Names);

/// <summary>
/// The message shapes, serialized without reflection so a Native-AOT member can send and read them.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DnsSelf))]
[JsonSerializable(typeof(DnsNames))]
[JsonSerializable(typeof(DnsNameEntry))]
public sealed partial class DnsJsonContext : JsonSerializerContext
{
    /// <summary>Read a payload off an envelope, or <see langword="null"/> when it is not that shape.</summary>
    /// <remarks>
    /// A malformed payload is answered with null rather than an exception: a handler that threw would
    /// have the sender retry forever against a payload that can never become valid.
    /// </remarks>
    public static T? Read<T>(JsonElement payload, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
        where T : class
    {
        try
        {
            return payload.Deserialize(type);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
