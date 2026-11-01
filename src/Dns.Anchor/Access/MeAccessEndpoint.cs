using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Http;

using TheKrystalShip.Auth.Sessions;

namespace TheKrystalShip.KGSM.Dns.Anchor.Access;

/// <summary>
/// <c>GET /me/access</c>: what the caller may do with this anchor, which is the <c>dns:*</c> actions
/// their token lists.
/// </summary>
/// <remarks>
/// Any person with a valid token is let through, because somebody holding nothing is exactly who needs to
/// see that. The answer is read from the token, so a role change reaches the person at their next
/// renewal.
/// </remarks>
internal static class MeAccessEndpoint
{
    public const string Path = "/me/access";

    public static void Map(WebApplication app) =>
        app.MapGet(Path, (Delegate)Handle).RequirePerson();

    internal static IResult Handle(HttpContext context) => Results.Json(ReportFor(SessionPerson.Of(context)!),
        AnchorJsonContext.Default.DnsAccessReport);

    internal static DnsAccessReport ReportFor(SessionPerson person) =>
        new(Current: true,
            Cluster: [.. person.Actions
                .Where(a => a.StartsWith(DnsActions.Namespace + ":", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)],
            Nodes: new Dictionary<string, IReadOnlyList<string>>(),
            Instances: new Dictionary<string, IReadOnlyList<string>>(),
            Owner: false);
}

/// <summary>The answer to <c>GET /me/access</c>: the actions allowed cluster-wide, as clients read the report.</summary>
internal sealed record DnsAccessReport(
    [property: JsonPropertyName("current")] bool Current,
    [property: JsonPropertyName("cluster")] IReadOnlyList<string> Cluster,
    [property: JsonPropertyName("nodes")] IReadOnlyDictionary<string, IReadOnlyList<string>> Nodes,
    [property: JsonPropertyName("instances")] IReadOnlyDictionary<string, IReadOnlyList<string>> Instances,
    [property: JsonPropertyName("owner")] bool Owner);
