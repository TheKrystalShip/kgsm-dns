using System.Text.Json.Serialization;

namespace TheKrystalShip.KGSM.Dns.Anchor;

/// <summary>
/// What this daemon is, answered to anybody who asks.
/// </summary>
/// <remarks>
/// Unauthenticated and deliberately thin. It says the daemon is up, which member it is, and whether it
/// is in a position to publish anything — what a health check and an operator both ask. It says nothing
/// about the names it holds or the credential it holds them with, both of which are behind the member
/// wire.
/// </remarks>
/// <param name="Name">Always <c>kgsm-dns</c>. What a client matches on.</param>
/// <param name="Member">This anchor's cluster member id.</param>
/// <param name="Capability">The capability it stands for.</param>
/// <param name="Clustered">Whether it holds a cluster secret at all.</param>
/// <param name="Configured">Whether a zone and a credential are present.</param>
/// <param name="Zone">The zone it publishes in, or empty when none is configured.</param>
internal sealed record AnchorIdentity(
    string Name,
    string Member,
    string Capability,
    bool Clustered,
    bool Configured,
    string Zone);

/// <summary>Why a request was not answered, for a browser to show.</summary>
/// <param name="Error">A stable code a client matches on.</param>
/// <param name="Message">The reason, in a sentence a person can act on.</param>
internal sealed record AnchorError(string Error, string Message);

/// <summary>
/// Every shape this daemon serializes.
/// </summary>
/// <remarks>
/// There is no reflection fallback under Native AOT, so a type missing here throws when it is first
/// serialized rather than failing the build.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AnchorIdentity))]
[JsonSerializable(typeof(AnchorError))]
[JsonSerializable(typeof(Access.DnsAccessReport))]
[JsonSerializable(typeof(Access.OperationManifest))]
internal sealed partial class AnchorJsonContext : JsonSerializerContext;
