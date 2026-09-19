using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Dns;
using TheKrystalShip.KGSM.Dns.Anchor.Registry;
using TheKrystalShip.KGSM.Dns.Messages;

namespace TheKrystalShip.KGSM.Dns.Anchor;

/// <summary>
/// <c>GET /api/v1/dns/names</c> — a member fetching its own names.
/// </summary>
/// <remarks>
/// <para>
/// <b>The names are fetched as well as pushed, because a push needs the member reachable.</b> A member is
/// reached at the names this anchor gave it, so one whose site is not serving them — a node that has just
/// joined, or one named again after a removal — cannot be called until it has been told what to serve.
/// It can always call out, and asks here.
/// </para>
/// <para>
/// <b>Answered only once the caller has said where it is reached.</b> Until this anchor has handled that
/// statement it holds no names for the member, and an empty answer to a member racing its own statement
/// would read as "you hold nothing" and withdraw everything it serves. The refusal is the member's cue to
/// ask again, not a verdict.
/// </para>
/// <para>
/// Keyed on the member the token names, like every other exchange: a member can only fetch its own.
/// </para>
/// </remarks>
internal static class NamesEndpoint
{
    public const string Path = DnsRoutes.Names;

    public static async Task HandleAsync(HttpContext context)
    {
        if (await ClusterRequest.AuthenticateAsync(context).ConfigureAwait(false) is not { } caller)
            return;

        IServiceProvider services = context.RequestServices;
        AnchorOptions options = services.GetRequiredService<AnchorOptions>();
        ClusterStateStore state = services.GetRequiredService<ClusterStateStore>();

        if (!options.Configured
            || !await state.IsHolderAsync(DnsCapability.Name, context.RequestAborted).ConfigureAwait(false))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        if (services.GetRequiredService<SqliteNameRegistry>().HostOf(caller.MemberId) is null)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        DnsNames names = services.GetRequiredService<NamesAnnouncer>().NamesOf(caller.MemberId);
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(
                context.Response.Body, names, DnsJsonContext.Default.DnsNames, context.RequestAborted)
            .ConfigureAwait(false);
    }
}
