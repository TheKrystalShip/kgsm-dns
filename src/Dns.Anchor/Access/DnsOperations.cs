using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using TheKrystalShip.KGSM.Sessions;

namespace TheKrystalShip.KGSM.Dns.Anchor.Access;

/// <summary>The <c>dns:*</c> action a route requires, declared on the route as metadata and published by <see cref="DnsOperations"/>.</summary>
/// <param name="Action">The action, checked cluster-wide.</param>
internal sealed record DnsAction(string Action);

/// <summary>Declares a route's <c>dns:*</c> action: published at <c>/operations</c> and enforced by the token.</summary>
internal static class DnsGate
{
    public static TBuilder Gated<TBuilder>(this TBuilder builder, string action)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new DnsAction(action)).RequireAction(action);
}

/// <summary>
/// What this anchor publishes at <c>GET /operations</c>: every gated route and the <c>dns:*</c> action it
/// requires, built from the routes' own <see cref="DnsAction"/> — the declaration each handler enforces —
/// so a client gates a control on the request it is about to make and names no action itself.
/// </summary>
internal static class DnsOperations
{
    /// <summary>Where the document is served.</summary>
    public const string Path = "/operations";

    public static void Map(WebApplication app) => app.MapGet(Path, Serve);

    private static Task Serve(HttpContext context) =>
        context.Response.WriteAsJsonAsync(
            Build(context.RequestServices.GetRequiredService<EndpointDataSource>()),
            AnchorJsonContext.Default.OperationManifest, contentType: null, context.RequestAborted);

    public static OperationManifest Build(EndpointDataSource endpoints)
    {
        var ops = new List<Operation>();
        foreach (RouteEndpoint e in endpoints.Endpoints.OfType<RouteEndpoint>())
        {
            if (e.Metadata.GetMetadata<DnsAction>() is not { } declared)
                continue;
            foreach (string method in e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
            {
                ops.Add(new Operation(method.ToUpperInvariant(), NormalizeRoute(e.RoutePattern.RawText ?? ""),
                    declared.Action, Operation.ClusterScope));
            }
        }

        Operation[] sorted = [.. ops
            .Distinct()
            .OrderBy(o => o.Route, StringComparer.Ordinal)
            .ThenBy(o => o.Method, StringComparer.Ordinal)];
        return new OperationManifest(OperationManifest.CurrentSchemaVersion, "", sorted);
    }

    private static string NormalizeRoute(string route)
    {
        string trimmed = route.Trim().Trim('/');
        return trimmed.Length == 0 ? "/" : "/" + trimmed;
    }
}

/// <summary>The operations this anchor serves, as a client reads them: <c>{ schemaVersion, base, operations }</c>.</summary>
internal sealed record OperationManifest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("base")] string Base,
    [property: JsonPropertyName("operations")] IReadOnlyList<Operation> Operations)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>One gated request: its method, route template, the action it needs and where that is checked.</summary>
internal sealed record Operation(
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("route")] string Route,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("scope")] string Scope)
{
    /// <summary>Every action here is checked cluster-wide.</summary>
    public const string ClusterScope = "cluster";
}
