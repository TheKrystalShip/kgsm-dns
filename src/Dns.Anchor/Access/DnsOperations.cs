using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using TheKrystalShip.KGSM.Auth.Access;

namespace TheKrystalShip.KGSM.Dns.Anchor.Access;

/// <summary>
/// The <c>dns:*</c> action a route requires, declared on the route and read by its handler to enforce.
/// </summary>
/// <param name="Action">The action, checked cluster-wide.</param>
internal sealed record DnsAction(string Action)
{
    /// <summary>The action the current route declares.</summary>
    public static string Of(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<DnsAction>()?.Action
        ?? throw new InvalidOperationException($"{context.Request.Method} {context.Request.Path} declares no action.");
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
            AccessJsonContext.Default.OperationManifest, contentType: null, context.RequestAborted);

    public static OperationManifest Build(EndpointDataSource endpoints)
    {
        var ops = new List<Operation>();
        foreach (RouteEndpoint e in endpoints.Endpoints.OfType<RouteEndpoint>())
        {
            if (e.Metadata.GetMetadata<DnsAction>() is not { } declared)
                continue;
            foreach (string method in e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
            {
                ops.Add(new Operation(method, OperationManifest.NormalizeRoute(e.RoutePattern.RawText ?? ""),
                    declared.Action, Operation.Scopes.Cluster));
            }
        }

        return OperationManifest.Of("", ops);
    }
}
