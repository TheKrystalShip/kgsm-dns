using System.Text.Json.Serialization;

using TheKrystalShip.KGSM.Dns.Messages;

namespace TheKrystalShip.KGSM.Dns.Status;

/// <summary>The routes the DNS anchor answers a person's browser on.</summary>
/// <remarks>
/// Served by the anchor itself and verified against the cluster's sessions, so a cluster whose nodes run
/// no Control Panel API still has a page for its names. The reads are open to a viewer; the writes below
/// need admin and never touch a name this cluster does not already own the base of.
/// </remarks>
public static class DnsStatusRoutes
{
    /// <summary>Everything the anchor holds about the cluster's names, as one document.</summary>
    public const string Status = "/api/v1/cluster/dns";

    /// <summary>The newest lines of the anchor's own event journal.</summary>
    public const string Events = "/api/v1/cluster/dns/events";

    /// <summary>Ask for a zone pass sooner than the timer would run one.</summary>
    public const string ZoneCheck = "/api/v1/cluster/dns/zone/check";

    /// <summary>Ask for a certificate to be renewed now. <c>{name}</c> is the certificate's own name.</summary>
    public const string CertificateRenew = "/api/v1/cluster/dns/certificates/{name}/renew";

    /// <summary>Name an existing game server under an additional label.</summary>
    public const string Aliases = "/api/v1/cluster/dns/aliases";

    /// <summary>Give up an alias. <c>{name}</c> is the alias's own fully qualified name.</summary>
    public const string Alias = "/api/v1/cluster/dns/aliases/{name}";
}

/// <summary>
/// What the cluster's DNS anchor holds, as of one moment.
/// </summary>
/// <remarks>
/// <para>
/// <b>Answered by whichever member serves the request</b>, and a member standing by for the capability
/// answers too: its <see cref="DnsStanding.Holds"/> is false and its registry is whatever it last held,
/// which is exactly what a person looking at the wrong member needs to see.
/// </para>
/// <para>
/// <b>Unknown is never none.</b> Every field that can be unknown is nullable, and null means the anchor
/// has not measured it — a pass that has not run, a certificate issued before anything recorded it, a host
/// that did not resolve. An empty list is a measured empty.
/// </para>
/// </remarks>
/// <param name="At">When this document was assembled.</param>
/// <param name="Standing">This member's place in the cluster and the configuration it publishes with.</param>
/// <param name="Zone">How the zone is being kept.</param>
/// <param name="Names">Every name in the registry, in name order.</param>
/// <param name="Members">Every member the anchor knows of, from its roster, its stated hosts and its names.</param>
/// <param name="Certificates">What the anchor has ordered from the issuer.</param>
public sealed record DnsStatus(
    DateTimeOffset At,
    DnsStanding Standing,
    DnsZoneStatus Zone,
    IReadOnlyList<DnsNameStatus> Names,
    IReadOnlyList<DnsMemberStatus> Members,
    DnsCertificatesStatus Certificates);

/// <summary>This member's place in the cluster, and what it publishes with.</summary>
/// <param name="Member">This anchor's member id.</param>
/// <param name="Holder">The member the cluster assigns the <c>dns</c> capability to, or null when none is.</param>
/// <param name="Holds">Whether this member is that holder. A member standing by writes nothing.</param>
/// <param name="Clustered">Whether this member holds a cluster secret.</param>
/// <param name="Configured">Whether a zone and a provider credential are present.</param>
/// <param name="Zone">The zone names are published in, or empty.</param>
/// <param name="Bases">The labels each kind of name lives under.</param>
/// <param name="TtlSeconds">How long resolvers may cache a name.</param>
/// <param name="ReconcileSeconds">How often the zone is compared with the registry.</param>
/// <param name="AcmeDirectory">The certificate issuer's directory.</param>
/// <param name="PublicHost">Where this anchor's own machine is reached, from its configuration.</param>
public sealed record DnsStanding(
    string Member,
    string? Holder,
    bool Holds,
    bool Clustered,
    bool Configured,
    string Zone,
    DnsBases Bases,
    int TtlSeconds,
    int ReconcileSeconds,
    string AcmeDirectory,
    string PublicHost);

/// <summary>The three labels names live under, each inside the zone.</summary>
public sealed record DnsBases(string Nodes, string Anchors, string Play);

/// <summary>How the zone is being kept.</summary>
/// <param name="Read">Whether this member has read its registry against the zone since it started.</param>
/// <param name="LastPass">The most recent pass, or null when none has run since the process started.</param>
/// <param name="LastSucceededAt">When a pass last completed, or null when none has since the process started.</param>
/// <param name="NextPassAt">When the next pass is due, or null when this member runs none.</param>
/// <param name="Contested">Names a record without this cluster's mark holds, as of the last pass.</param>
public sealed record DnsZoneStatus(
    bool Read,
    DnsZonePass? LastPass,
    DateTimeOffset? LastSucceededAt,
    DateTimeOffset? NextPassAt,
    IReadOnlyList<string> Contested);

/// <summary>One comparison of the registry with the zone.</summary>
/// <param name="StartedAt">When it began.</param>
/// <param name="DurationMs">How long it took.</param>
/// <param name="Succeeded">Whether it completed.</param>
/// <param name="Error">Why it did not, in one line. Null when it did.</param>
/// <param name="Published">Records created.</param>
/// <param name="Moved">Records corrected.</param>
/// <param name="Withdrawn">Records deleted.</param>
/// <param name="Recovered">Registry rows rebuilt from the zone's marks.</param>
public sealed record DnsZonePass(
    DateTimeOffset StartedAt,
    int DurationMs,
    bool Succeeded,
    string? Error,
    int Published,
    int Moved,
    int Withdrawn,
    int Recovered);

/// <summary>One name the cluster has claimed.</summary>
/// <param name="Name">The fully qualified name.</param>
/// <param name="Kind"><c>member</c>, <c>capability</c>, <c>game</c>, <c>alias</c> or <c>capability-alias</c>.</param>
/// <param name="Member">The member it belongs to.</param>
/// <param name="Key">The capability or instance within that member, empty for a node's own name.</param>
/// <param name="Type"><c>A</c>, <c>AAAA</c> or <c>CNAME</c>.</param>
/// <param name="Content">What it points at, or empty while nothing can be.</param>
/// <param name="State"><c>pending</c>, <c>published</c> or <c>blocked</c>.</param>
/// <param name="Note">Why a blocked name is blocked. Empty otherwise.</param>
/// <param name="Recorded">Whether the provider holds a record for it.</param>
/// <param name="UpdatedAt">When the registry row was last written.</param>
/// <param name="Ports">A game name's declared ports, null for any other kind or when none were stated.</param>
/// <param name="Collisions">
/// A game name's servers on other members behind the same public address that want an overlapping port:
/// empty for none, null when it could not be told or is not a game name. Servers on one node are never
/// compared here — the engine answers for those.
/// </param>
/// <param name="AliasOf">
/// The name an alias follows: a game server's name for an alias, a capability's for a capability alias.
/// Null for any other kind.
/// </param>
public sealed record DnsNameStatus(
    string Name,
    string Kind,
    string Member,
    string Key,
    string Type,
    string Content,
    string State,
    string Note,
    bool Recorded,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<DnsPort>? Ports,
    IReadOnlyList<DnsCollision>? Collisions,
    string? AliasOf);

/// <summary>One member as the DNS anchor knows it.</summary>
/// <param name="Member">The member id.</param>
/// <param name="Kind"><c>node</c> or <c>anchor</c> from the roster, or null when the roster does not hold it.</param>
/// <param name="Label">The roster's nickname for it, or null.</param>
/// <param name="Membership">The roster's gossip state for it, or null when the roster does not hold it.</param>
/// <param name="Host">Where it said it is reached, empty for "nowhere public", null when it never said.</param>
/// <param name="StatedAt">When it last said so, or null for this anchor, whose host is configured.</param>
/// <param name="Self">Whether this is the anchor answering.</param>
/// <param name="Addresses">
/// What its host resolved to at the last collision check, or null when it was not resolved. Two members
/// sharing an address are behind one router and share its port space.
/// </param>
public sealed record DnsMemberStatus(
    string Member,
    string? Kind,
    string? Label,
    string? Membership,
    string? Host,
    DateTimeOffset? StatedAt,
    bool Self,
    IReadOnlyList<string>? Addresses);

/// <summary>What the anchor has ordered from the certificate issuer.</summary>
/// <param name="Orders">The newest order for each name, and every order waiting or in flight.</param>
/// <param name="IssuedLastWeek">
/// Certificates issued in the last seven days. The issuer limits certificates per registered domain per
/// week, and every certificate in a cluster is ordered here.
/// </param>
/// <param name="FailedLastWeek">Orders that ended without a certificate in the last seven days.</param>
public sealed record DnsCertificatesStatus(
    IReadOnlyList<DnsCertificateOrder> Orders,
    int IssuedLastWeek,
    int FailedLastWeek);

/// <summary>One certificate order.</summary>
/// <param name="Name">The name it certifies.</param>
/// <param name="Member">The member it was ordered for.</param>
/// <param name="State"><c>queued</c>, <c>issuing</c>, <c>issued</c> or <c>failed</c>.</param>
/// <param name="RequestedAt">When the member asked.</param>
/// <param name="FinishedAt">When the order ended, or null while it has not.</param>
/// <param name="NotAfter">When the issued certificate expires, or null when none was issued.</param>
/// <param name="RenewsFrom">
/// When the member will ask for a new certificate — the start of this one's last third. Null when no
/// certificate was issued, or when the order predates this being recorded.
/// </param>
/// <param name="Failure">Why no certificate was issued, in one line. Null otherwise.</param>
public sealed record DnsCertificateOrder(
    string Name,
    string Member,
    string State,
    DateTimeOffset RequestedAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset? NotAfter,
    DateTimeOffset? RenewsFrom,
    string? Failure);

/// <summary>What a browser posts to name an existing game server or capability under an additional label.</summary>
/// <param name="Server">
/// The name to alias: a game name the cluster holds, or a capability's name under the anchors base.
/// </param>
/// <param name="Label">
/// The label the alias takes: inside the play base beside a game server, directly under the zone for a
/// capability.
/// </param>
public sealed record DnsAliasRequest(string Server, string Label);

/// <summary>Every status shape, serialized without reflection.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(DnsStatus))]
[JsonSerializable(typeof(DnsNameStatus))]
[JsonSerializable(typeof(DnsAliasRequest))]
public sealed partial class DnsStatusJsonContext : JsonSerializerContext;
