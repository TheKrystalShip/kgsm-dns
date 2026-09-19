using System.Globalization;
using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Dns.Anchor.Access;
using TheKrystalShip.KGSM.Dns.Anchor.Issuance;
using TheKrystalShip.KGSM.Dns.Anchor.Names;
using TheKrystalShip.KGSM.Dns.Anchor.Registry;
using TheKrystalShip.KGSM.Dns.Messages;
using TheKrystalShip.KGSM.Dns.Status;
using TheKrystalShip.KGSM.Events;
using TheKrystalShip.KGSM.Extensions;

namespace TheKrystalShip.KGSM.Dns.Anchor.Status;

/// <summary>
/// What a person's browser reads from this anchor: <see cref="DnsStatusRoutes.Status"/> and
/// <see cref="DnsStatusRoutes.Events"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reads only.</b> Nothing a browser sends here reaches the zone, the issuer or the registry; the
/// anchor's writes stay driven by the members that state themselves and by the reconcile timer.
/// </para>
/// <para>
/// <b>Answered by any member running this daemon</b>, the holder or one standing by, each from what it
/// holds. A member standing by says so in its standing, which is what someone looking at the wrong member
/// needs to be told.
/// </para>
/// </remarks>
internal static class StatusEndpoints
{
    /// <summary>How many journal lines a browser gets when it does not say.</summary>
    private const int DefaultEvents = 100;

    /// <summary>The most journal lines one request is answered with.</summary>
    private const int MostEvents = 500;

    public static void Map(WebApplication app)
    {
        // Cast to Delegate so each binds as a route handler, whose returned result is what is sent. Bound as
        // a request delegate — the overload a lone HttpContext parameter picks — the result is discarded.
        app.MapGet(DnsStatusRoutes.Status, (Delegate)StatusAsync);
        app.MapGet(DnsStatusRoutes.Events, (Delegate)EventsAsync);
    }

    private static async Task<IResult> StatusAsync(HttpContext context)
    {
        IServiceProvider services = context.RequestServices;
        CancellationToken ct = context.RequestAborted;

        if (await services.GetRequiredService<ClusterBearer>().RefuseAsync(context.Request, ct)
                .ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }

        DnsStatus status = await BuildAsync(services, ct).ConfigureAwait(false);
        return Results.Json(status, DnsStatusJsonContext.Default.DnsStatus);
    }

    private static async Task<DnsStatus> BuildAsync(IServiceProvider services, CancellationToken ct)
    {
        AnchorOptions options = services.GetRequiredService<AnchorOptions>();
        ClusterOptions cluster = services.GetRequiredService<ClusterOptions>();
        SqliteNameRegistry registry = services.GetRequiredService<SqliteNameRegistry>();
        PortCollisions collisions = services.GetRequiredService<PortCollisions>();

        string? holder = await services.GetRequiredService<ClusterStateStore>()
            .HolderAsync(DnsCapability.Name, ct).ConfigureAwait(false);

        var standing = new DnsStanding(
            Member: options.MemberId,
            Holder: holder,
            Holds: string.Equals(holder, options.MemberId, StringComparison.Ordinal),
            Clustered: cluster.Enabled,
            Configured: options.Configured,
            Zone: options.Bases.Zone,
            Bases: new DnsBases(options.Bases.Nodes, options.Bases.Anchors, options.Bases.Play),
            TtlSeconds: options.Ttl,
            ReconcileSeconds: (int)options.Reconcile.TotalSeconds,
            AcmeDirectory: options.AcmeDirectory,
            PublicHost: options.PublicHost);

        DnsZoneStatus zone = services.GetRequiredService<ZonePassLog>()
            .Snapshot(services.GetRequiredService<RecordReconciler>().Ready);

        IReadOnlyList<NameRow> rows = registry.All();
        IReadOnlyDictionary<string, IReadOnlyList<DnsPort>> ports = registry.AllPorts();
        List<DnsNameStatus> names =
        [
            .. rows.Select(r => new DnsNameStatus(
                Name: r.Name,
                Kind: NameKinds.Spell(r.Kind),
                Member: r.Member,
                Key: r.Key,
                Type: r.Type,
                Content: r.Content,
                State: Spell(r.State),
                Note: r.Note,
                Recorded: !string.IsNullOrEmpty(r.RecordId),
                UpdatedAt: r.UpdatedAt,
                Ports: r.Kind == NameKind.Game && ports.TryGetValue(r.Name, out IReadOnlyList<DnsPort>? p) ? p : null,
                Collisions: r.Kind == NameKind.Game ? collisions.For(r.Name) : null)),
        ];

        IReadOnlyList<MemberRow> roster = await services.GetRequiredService<MembersStore>()
            .ListAsync(ct).ConfigureAwait(false);
        List<DnsMemberStatus> members = Members(options, roster, registry.StatedHosts(), rows, collisions);

        IssuanceQueue queue = services.GetRequiredService<IssuanceQueue>();
        CertificateOrders orders = services.GetRequiredService<CertificateOrders>();
        var byName = orders.Latest().ToDictionary(o => o.Name, StringComparer.OrdinalIgnoreCase);
        foreach (DnsCertificateOrder pending in queue.Pending())
            byName[pending.Name] = pending;

        var certificates = new DnsCertificatesStatus(
            [.. byName.Values.OrderBy(o => o.Name, StringComparer.Ordinal)],
            orders.IssuedSince(DateTimeOffset.UtcNow.AddDays(-7)));

        return new DnsStatus(DateTimeOffset.UtcNow, standing, zone, names, members, certificates);
    }

    /// <summary>
    /// Every member the anchor knows of: its roster, every member that stated a host, and every member a
    /// name belongs to — which can differ, since a member offline keeps its names and a removed one keeps
    /// them until its departure settles.
    /// </summary>
    private static List<DnsMemberStatus> Members(
        AnchorOptions options,
        IReadOnlyList<MemberRow> roster,
        IReadOnlyList<(string Member, string Host, DateTimeOffset StatedAt)> stated,
        IReadOnlyList<NameRow> rows,
        PortCollisions collisions)
    {
        Dictionary<string, MemberRow> byId = roster
            .GroupBy(r => r.MemberId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        Dictionary<string, (string Host, DateTimeOffset StatedAt)> hosts = stated
            .ToDictionary(s => s.Member, s => (s.Host, s.StatedAt), StringComparer.Ordinal);

        IEnumerable<string> ids = byId.Keys
            .Concat(hosts.Keys)
            .Concat(rows.Select(r => r.Member))
            .Append(options.MemberId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal);

        var members = new List<DnsMemberStatus>();
        foreach (string id in ids)
        {
            byId.TryGetValue(id, out MemberRow? row);
            bool self = string.Equals(id, options.MemberId, StringComparison.Ordinal);

            // This anchor states no host to itself: where it is reached is its own configuration.
            string? host = self ? options.PublicHost : hosts.TryGetValue(id, out var h) ? h.Host : null;
            DateTimeOffset? statedAt = !self && hosts.TryGetValue(id, out var s) ? s.StatedAt : null;

            members.Add(new DnsMemberStatus(
                Member: id,
                // A member's roster does not list the member itself, and this one knows what it is.
                Kind: self ? "anchor" : row?.Kind,
                Label: string.IsNullOrWhiteSpace(row?.Nickname) ? null : row.Nickname,
                Membership: row?.MembershipState,
                Host: host,
                StatedAt: statedAt,
                Self: self,
                Addresses: collisions.AddressesOf(id) is { } addresses
                    ? [.. addresses.Select(a => a.ToString()).Order(StringComparer.Ordinal)]
                    : null));
        }

        return members;
    }

    private static async Task<IResult> EventsAsync(HttpContext context)
    {
        IServiceProvider services = context.RequestServices;
        CancellationToken ct = context.RequestAborted;

        if (await services.GetRequiredService<ClusterBearer>().RefuseAsync(context.Request, ct)
                .ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }

        int limit = int.TryParse(context.Request.Query["limit"], NumberStyles.Integer, CultureInfo.InvariantCulture,
            out int asked) && asked > 0
            ? Math.Min(asked, MostEvents)
            : DefaultEvents;

        string directory = JournalLayout.DirectoryFor(
            DnsJournal.ProducerId,
            Environment.GetEnvironmentVariable(JournalServiceCollectionExtensions.StateRootVariable));

        List<string> lines;
        try
        {
            lines = Newest(directory, limit);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Results.Json(
                new AnchorError("journal_unreadable", "this anchor could not read its own journal"),
                AnchorJsonContext.Default.AnchorError,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // Each line is written as the journal holds it: the envelope is the ecosystem's, and a reader that
        // understands one producer's journal understands this one's.
        context.Response.ContentType = "application/json";
        await using var writer = new Utf8JsonWriter(context.Response.Body);
        writer.WriteStartObject();
        writer.WriteStartArray("events");
        foreach (string line in lines)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                document.RootElement.WriteTo(writer);
            }
            catch (JsonException)
            {
                // A torn line is one the writer is still appending, or one a crash cut short. Neither is
                // an event yet.
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync(ct).ConfigureAwait(false);
        return Results.Empty;
    }

    /// <summary>The newest lines across the journal's daily segments, newest first.</summary>
    private static List<string> Newest(string directory, int limit)
    {
        var lines = new List<string>(limit);
        if (!Directory.Exists(directory))
            return lines;

        // Segments are named by date, so name order is time order.
        foreach (string segment in Directory.GetFiles(directory, "*.ndjson").OrderDescending(StringComparer.Ordinal))
        {
            string[] all = File.ReadAllLines(segment);
            for (int i = all.Length - 1; i >= 0 && lines.Count < limit; i--)
            {
                if (!string.IsNullOrWhiteSpace(all[i]))
                    lines.Add(all[i]);
            }

            if (lines.Count >= limit)
                break;
        }

        return lines;
    }

    private static string Spell(NameState state) => state switch
    {
        NameState.Published => "published",
        NameState.Blocked => "blocked",
        _ => "pending",
    };
}
