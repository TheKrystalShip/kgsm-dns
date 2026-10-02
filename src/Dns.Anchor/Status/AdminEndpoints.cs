using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Cluster.Messaging;
using TheKrystalShip.KGSM.Dns;
using TheKrystalShip.KGSM.Dns.Anchor.Access;
using TheKrystalShip.KGSM.Dns.Anchor.Names;
using TheKrystalShip.KGSM.Dns.Anchor.Registry;
using TheKrystalShip.KGSM.Dns.Messages;
using TheKrystalShip.KGSM.Dns.Serving;
using TheKrystalShip.KGSM.Dns.Status;

namespace TheKrystalShip.KGSM.Dns.Anchor.Status;

/// <summary>
/// What a person's browser writes through this anchor: <see cref="DnsStatusRoutes.ZoneCheck"/>,
/// <see cref="DnsStatusRoutes.CertificateRenew"/> and <see cref="DnsStatusRoutes.Aliases"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each route's own action, and only on the holder.</b> A zone check needs
/// <see cref="DnsActions.ZoneCheck"/>, a renewal <see cref="DnsActions.CertificatesRenew"/>, adding or
/// removing an alias <see cref="DnsActions.AliasesWrite"/>, each evaluated here rather than inferred from
/// being allowed to read the page. A member standing by for the capability
/// writes nothing — two writers would each reconcile the other's records away — so every route refuses
/// with a body naming why before touching the registry, the zone or a member.
/// </para>
/// <para>
/// <b>Nothing here points a name anywhere new.</b> A game server's alias is claimed at the same node its
/// server already resolves to, through <see cref="NameService.ClaimAlias"/>, and a capability's alias at
/// the same holder its capability name does, through <see cref="NameService.ClaimCapabilityAlias"/> —
/// the same entitlement the zone pass enforces for every other name. A renewal asks a member for a
/// certificate it already holds published, never a new one.
/// </para>
/// </remarks>
internal static class AdminEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost(DnsStatusRoutes.ZoneCheck, (Delegate)ZoneCheckAsync).WithMetadata(new DnsAction(DnsActions.ZoneCheck));
        app.MapPost(DnsStatusRoutes.CertificateRenew, (Delegate)RenewAsync).WithMetadata(new DnsAction(DnsActions.CertificatesRenew));
        app.MapPost(DnsStatusRoutes.Aliases, (Delegate)AddAliasAsync).WithMetadata(new DnsAction(DnsActions.AliasesWrite));
        app.MapDelete(DnsStatusRoutes.Alias, (Delegate)RemoveAliasAsync).WithMetadata(new DnsAction(DnsActions.AliasesWrite));
    }

    private static async Task<IResult> ZoneCheckAsync(HttpContext context)
    {
        IServiceProvider services = context.RequestServices;
        CancellationToken ct = context.RequestAborted;

        ClusterBearer.Admission admission = await Authorize(services, context, ct).ConfigureAwait(false);
        if (admission.Refusal is { } refusal)
            return refusal;

        if (!await IsHolderReadyAsync(services, ct).ConfigureAwait(false))
            return NotConfiguredOrRefusal(services);

        services.GetRequiredService<ClusterMembershipWorker>().RequestZonePass();
        await services.GetRequiredService<DnsJournal>()
            .ZoneCheckRequestedAsync(admission.Identity!.ActorString, ct).ConfigureAwait(false);
        return Results.Accepted();
    }

    private static async Task<IResult> RenewAsync(HttpContext context)
    {
        IServiceProvider services = context.RequestServices;
        CancellationToken ct = context.RequestAborted;

        ClusterBearer.Admission admission = await Authorize(services, context, ct).ConfigureAwait(false);
        if (admission.Refusal is { } refusal)
            return refusal;

        if (!await IsHolderReadyAsync(services, ct).ConfigureAwait(false))
            return NotConfiguredOrRefusal(services);

        string? name = Name(context);
        if (name is null)
            return Results.NotFound();

        SqliteNameRegistry registry = services.GetRequiredService<SqliteNameRegistry>();
        NameRow? row = registry.Find(name);
        if (row is null)
            return Results.NotFound();

        if (!NameKinds.TakesCertificate(row.Kind))
            return Conflict("no_certificate", name + " is a game name and takes no certificate");

        AnchorOptions options = services.GetRequiredService<AnchorOptions>();
        if (string.Equals(row.Member, options.MemberId, StringComparison.Ordinal))
        {
            bool asked = await services.GetRequiredService<CertificateKeeper>()
                .ForceAsync(name, ct).ConfigureAwait(false);
            if (!asked)
                return Conflict("not_published", name + " is not published yet — there is nothing to renew");
        }
        else
        {
            MemberRow? target = await services.GetRequiredService<MembersStore>()
                .GetByMemberIdAsync(row.Member, ct).ConfigureAwait(false);
            if (target is null || string.IsNullOrWhiteSpace(target.Url))
                return Conflict("unreachable", row.Member + " has no address this anchor can reach it at");

            await services.GetRequiredService<IClusterBus>().EnqueueAsync(
                DnsMessageTypes.Renew, new DnsRenew(name), DnsJsonContext.Default.DnsRenew,
                [new ClusterTarget(target.MemberId, target.Url)], ct).ConfigureAwait(false);
        }

        await services.GetRequiredService<DnsJournal>()
            .RenewRequestedAsync(name, row.Member, admission.Identity!.ActorString, ct).ConfigureAwait(false);
        return Results.Accepted();
    }

    private static async Task<IResult> AddAliasAsync(HttpContext context)
    {
        IServiceProvider services = context.RequestServices;
        CancellationToken ct = context.RequestAborted;

        ClusterBearer.Admission admission = await Authorize(services, context, ct).ConfigureAwait(false);
        if (admission.Refusal is { } refusal)
            return refusal;

        if (!await IsHolderReadyAsync(services, ct).ConfigureAwait(false))
            return NotConfiguredOrRefusal(services);

        DnsAliasRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync(
                context.Request.Body, DnsStatusJsonContext.Default.DnsAliasRequest, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Server) || string.IsNullOrWhiteSpace(request.Label))
            return Results.BadRequest();

        string label = request.Label.Trim().ToLowerInvariant();
        if (!DnsLabel.IsValid(label))
            return BadRequest("invalid_label", label + " cannot be published as a DNS label");

        SqliteNameRegistry registry = services.GetRequiredService<SqliteNameRegistry>();
        NameRow? server = registry.Find(request.Server.Trim().TrimEnd('.').ToLowerInvariant());
        if (server is null || server.Kind is not (NameKind.Game or NameKind.Capability))
            return NotFoundError("no_such_server",
                request.Server + " is not a game server or a capability this cluster names");

        ClusterBases bases = services.GetRequiredService<ClusterBases>();
        NameService names = services.GetRequiredService<NameService>();
        string name;
        NameRow? alias;
        if (server.Kind == NameKind.Game)
        {
            name = bases.Compose(NameKind.Alias, label);
            alias = names.ClaimAlias(name, server.Member, server.Key);
        }
        else
        {
            // At the top of the zone a label can also be one of the bases themselves, and a record there
            // would sit on the name every other cluster name hangs beneath.
            if (bases.IsBaseLabel(label))
                return BadRequest("invalid_label", label + " names one of this cluster's bases");

            name = bases.Compose(NameKind.CapabilityAlias, label);
            alias = names.ClaimCapabilityAlias(name, server.Key);
        }

        if (alias is null)
            return Conflict("name_held", name + " is already held");

        services.GetRequiredService<ClusterMembershipWorker>().RequestZonePass();
        await services.GetRequiredService<DnsJournal>()
            .AliasAddedAsync(name, server.Name, admission.Identity!.ActorString, ct).ConfigureAwait(false);

        var status = new DnsNameStatus(
            Name: alias.Name, Kind: NameKinds.Spell(alias.Kind), Member: alias.Member, Key: alias.Key,
            Type: alias.Type, Content: alias.Content, State: StatusEndpoints.Spell(alias.State), Note: alias.Note,
            Recorded: false, UpdatedAt: alias.UpdatedAt, Ports: null, Collisions: null, AliasOf: server.Name);
        return Results.Json(status, DnsStatusJsonContext.Default.DnsNameStatus, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> RemoveAliasAsync(HttpContext context)
    {
        IServiceProvider services = context.RequestServices;
        CancellationToken ct = context.RequestAborted;

        ClusterBearer.Admission admission = await Authorize(services, context, ct).ConfigureAwait(false);
        if (admission.Refusal is { } refusal)
            return refusal;

        if (!await IsHolderReadyAsync(services, ct).ConfigureAwait(false))
            return NotConfiguredOrRefusal(services);

        string? name = Name(context);
        if (name is null)
            return Results.NotFound();

        SqliteNameRegistry registry = services.GetRequiredService<SqliteNameRegistry>();
        NameRow? row = registry.Find(name);
        if (row is null || row.Kind is not (NameKind.Alias or NameKind.CapabilityAlias))
            return Results.NotFound();

        string server = row.Kind == NameKind.Alias
            ? registry.FindFor(NameKind.Game, row.Member, row.Key)?.Name ?? ""
            : services.GetRequiredService<ClusterBases>().Compose(NameKind.Capability, row.Key);

        services.GetRequiredService<NameService>().Release(name);
        services.GetRequiredService<ClusterMembershipWorker>().RequestZonePass();
        await services.GetRequiredService<DnsJournal>()
            .AliasRemovedAsync(name, server, admission.Identity!.ActorString, ct).ConfigureAwait(false);

        return Results.NoContent();
    }

    // The action is the one the route declares, which is also what this anchor publishes for it.
    private static Task<ClusterBearer.Admission> Authorize(IServiceProvider services, HttpContext context, CancellationToken ct) =>
        services.GetRequiredService<ClusterBearer>().AuthorizeAsync(context.Request, DnsAction.Of(context), ct);

    /// <summary>
    /// Whether this member is configured and holds the capability, so it is safe to write. Refusing before
    /// this is checked is what keeps a standing-by member from ever touching the zone, the registry or a
    /// member — the same rule the zone pass itself runs under.
    /// </summary>
    private static async Task<bool> IsHolderReadyAsync(IServiceProvider services, CancellationToken ct)
    {
        AnchorOptions options = services.GetRequiredService<AnchorOptions>();
        if (!options.Configured)
            return false;

        return await services.GetRequiredService<ClusterStateStore>()
            .IsHolderAsync(DnsCapability.Name, ct).ConfigureAwait(false);
    }

    private static IResult NotConfiguredOrRefusal(IServiceProvider services)
    {
        AnchorOptions options = services.GetRequiredService<AnchorOptions>();
        return options.Configured
            ? Conflict("standing_by", "this member does not hold the dns capability, so it writes nothing")
            : Unavailable("unconfigured", "no zone and credential are configured — there is nothing to write");
    }

    private static string? Name(HttpContext context) =>
        context.Request.RouteValues["name"] as string is { Length: > 0 } name
            ? name.Trim().TrimEnd('.').ToLowerInvariant()
            : null;

    private static IResult Unavailable(string error, string message) =>
        Results.Json(new AnchorError(error, message), AnchorJsonContext.Default.AnchorError,
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult Conflict(string error, string message) =>
        Results.Json(new AnchorError(error, message), AnchorJsonContext.Default.AnchorError,
            statusCode: StatusCodes.Status409Conflict);

    private static IResult NotFoundError(string error, string message) =>
        Results.Json(new AnchorError(error, message), AnchorJsonContext.Default.AnchorError,
            statusCode: StatusCodes.Status404NotFound);

    private static IResult BadRequest(string error, string message) =>
        Results.Json(new AnchorError(error, message), AnchorJsonContext.Default.AnchorError,
            statusCode: StatusCodes.Status400BadRequest);
}
