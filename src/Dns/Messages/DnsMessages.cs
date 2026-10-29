using System.Text.Json;
using System.Text.Json.Serialization;

namespace TheKrystalShip.KGSM.Dns.Messages;

/// <summary>The route a member fetches its own names from, on the member holding <c>dns</c>.</summary>
/// <remarks>
/// It answers with the same <see cref="DnsNames"/> the anchor pushes, and only once the caller has said
/// where it is reached: a member that has not been heard from yet holds nothing, and an empty answer then
/// would read as a verdict.
/// </remarks>
public static class DnsRoutes
{
    /// <summary>The path, answered on the holder's own address.</summary>
    public const string Names = "/api/v1/dns/names";
}

/// <summary>The bus message types this component speaks.</summary>
public static class DnsMessageTypes
{
    /// <summary>
    /// A member to the DNS anchor: where this member is reached from the internet. Sent whole every time,
    /// so the latest one is the whole truth and a lost or repeated one changes nothing.
    /// </summary>
    public const string Self = "dns.self";

    /// <summary>
    /// The DNS anchor to a member: every name the registry holds for that member, and where each
    /// stands. A complete set, for the same reason.
    /// </summary>
    public const string Names = "dns.names";

    /// <summary>
    /// A member to the DNS anchor: a signing request for one name the member holds. The key it was made
    /// with never leaves the member.
    /// </summary>
    public const string Csr = "dns.csr";

    /// <summary>The DNS anchor to a member: the certificate chain issued for one of its names.</summary>
    public const string Cert = "dns.cert";

    /// <summary>
    /// A node to the DNS anchor: every game server installed on it. The complete set, sent only from a
    /// successful read, so an instance missing from it has been uninstalled and its name is released.
    /// </summary>
    public const string Instances = "dns.instances";

    /// <summary>
    /// The DNS anchor to a member: ask for a certificate now, ahead of its own schedule. A person's request,
    /// not a fact about the name — the member decides for itself whether to act on it.
    /// </summary>
    public const string Renew = "dns.renew";
}

/// <summary>One port range a game server listens on.</summary>
/// <param name="Start">The first port.</param>
/// <param name="End">The last port; equal to <paramref name="Start"/> for a single port.</param>
/// <param name="Protocol">`tcp` or `udp`.</param>
public sealed record DnsPort(int Start, int End, string Protocol);

/// <summary>One game server on a node.</summary>
/// <param name="Instance">The instance id, which the engine mints at install and never changes.</param>
/// <param name="Blueprint">The game it runs; its name is suggested from this.</param>
/// <param name="Ports">Every port it declares.</param>
/// <param name="Inherits">The game server this one continues, set while a transfer's record exists on the
/// node holding the continuation. Omitted from the wire when null.</param>
public sealed record DnsInstance(
    string Instance, string Blueprint, IReadOnlyList<DnsPort> Ports,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DnsInheritance? Inherits = null);

/// <summary>The game server an instance continues: the member and instance it came from, and its game name.</summary>
/// <remarks>
/// The anchor reads it only for an instance that holds no name yet, so restating it changes nothing.
/// </remarks>
/// <param name="Member">The member the previous server ran on.</param>
/// <param name="Instance">The previous server's instance id.</param>
/// <param name="Name">The game name it held, or null when it held none.</param>
public sealed record DnsInheritance(string Member, string Instance, string? Name);

/// <summary>Every game server on one node.</summary>
/// <param name="Instances">The complete set. An empty list is a node with none installed.</param>
public sealed record DnsInstances(IReadOnlyList<DnsInstance> Instances);

/// <summary>A signing request for one name.</summary>
/// <param name="Name">The name the certificate is for, which the member must hold.</param>
/// <param name="Csr">The PKCS#10 request, DER, base64-encoded.</param>
public sealed record DnsCsr(string Name, string Csr);

/// <summary>A certificate issued for one name.</summary>
/// <param name="Name">The name it is for.</param>
/// <param name="Chain">The PEM chain, leaf first.</param>
public sealed record DnsCert(string Name, string Chain);

/// <summary>Ask a member for a certificate for one of its names, ahead of its own schedule.</summary>
/// <param name="Name">The name to renew. The member ignores this for any name it does not hold.</param>
public sealed record DnsRenew(string Name);

/// <summary>What a member says about itself.</summary>
/// <param name="PublicHost">
/// Where the member is reached from the internet: normally its network's dynamic-DNS name, or a fixed
/// public address. A node is named from it, and every capability the member holds points at it. Empty
/// is a real answer — a member that states no host holds its names unpublished.
/// </param>
public sealed record DnsSelf(string PublicHost);

/// <summary>One name a member holds.</summary>
/// <param name="Name">The fully qualified name.</param>
/// <param name="Kind">What it names, spelled as <see cref="NameKinds.Spell"/> spells it.</param>
/// <param name="Key">The capability or instance within the member, empty for a node's own name.</param>
/// <param name="State">`pending`, `published` or `blocked`.</param>
/// <param name="Note">Why a blocked name is blocked. Empty otherwise.</param>
/// <param name="Collisions">For a game name: every game server on another member reached at the same
/// public address that declares an overlapping port on the same protocol. Empty when there is none;
/// null when it could not be told, because a host involved could not be resolved — and for every name
/// that is not a game name.</param>
public sealed record DnsNameEntry(
    string Name, string Kind, string Key, string State, string Note,
    IReadOnlyList<DnsCollision>? Collisions = null)
{
    /// <summary>Whether the provider holds the record, so the name resolves.</summary>
    [JsonIgnore]
    public bool Published => string.Equals(State, "published", StringComparison.Ordinal);
}

/// <summary>
/// Another member's game server that wants the same port as this one on the same public address.
/// </summary>
/// <remarks>
/// A report, not a refusal: both names stay published. Whether both servers can run at once is decided by
/// the router in front of that address and the hosts behind it, not by DNS.
/// </remarks>
/// <param name="Name">The other server's game name.</param>
/// <param name="Member">The member it runs on.</param>
/// <param name="Start">The first port both declare.</param>
/// <param name="End">The last port both declare.</param>
/// <param name="Protocol">`tcp` or `udp`.</param>
public sealed record DnsCollision(string Name, string Member, int Start, int End, string Protocol);

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
[JsonSerializable(typeof(DnsCsr))]
[JsonSerializable(typeof(DnsCert))]
[JsonSerializable(typeof(DnsRenew))]
[JsonSerializable(typeof(DnsInstances))]
[JsonSerializable(typeof(DnsInheritance))]
[JsonSerializable(typeof(DnsCollision))]
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
