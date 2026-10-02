using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.KGSM.Auth.Access;
using TheKrystalShip.KGSM.Auth.Cluster;

namespace TheKrystalShip.KGSM.Dns.Anchor.Access;

/// <summary>
/// <c>GET /me/access</c>: what the caller may do with this anchor — its own <c>dns:*</c> actions,
/// cluster-wide — already evaluated from this member's replica of the authority.
/// </summary>
/// <remarks>
/// <para>
/// The session is verified the way every route here verifies one, and any usable account is let
/// through, because somebody holding nothing is exactly who needs to see that. What they may do is then
/// read from the replica, never from the session.
/// </para>
/// <para>
/// A replica that cannot be read is <c>503 authority_unavailable</c>: "nothing here can say" is not
/// "you may do nothing".
/// </para>
/// </remarks>
internal static class MeAccessEndpoint
{
    public const string Path = "/me/access";

    public static void Map(WebApplication app) =>
        app.MapGet(Path, (Delegate)HandleAsync);

    internal static async Task<IResult> HandleAsync(HttpContext context)
    {
        IServiceProvider services = context.RequestServices;
        CancellationToken ct = context.RequestAborted;

        ClusterBearer.Admission admission = await services.GetRequiredService<ClusterBearer>()
            .AuthorizeAsync(context.Request, action: null, ct).ConfigureAwait(false);
        if (admission.Refusal is { } refusal)
            return refusal;

        (MemberAccessCaller caller, AccessReport? report) = await services.GetRequiredService<MemberAccess>().ReportAsync(
            admission.Identity!, [AccessScope.Cluster],
            action => action.StartsWith(DnsActions.Namespace + ":", StringComparison.Ordinal), ct).ConfigureAwait(false);

        return caller.Refusal switch
        {
            MemberAccessRefusal.None => Results.Json(report!, AccessJsonContext.Default.AccessReport),
            MemberAccessRefusal.Unavailable => Error(StatusCodes.Status503ServiceUnavailable, "authority_unavailable",
                caller.Reason ?? "This anchor's authority replica could not be read."),
            MemberAccessRefusal.AccountDisabled => Error(StatusCodes.Status403Forbidden, "account_disabled",
                "This account has been switched off."),
            _ => Error(StatusCodes.Status403Forbidden, "no_account", "No account here matches this session."),
        };
    }

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new AnchorError(code, message), AnchorJsonContext.Default.AnchorError, statusCode: status);
}
