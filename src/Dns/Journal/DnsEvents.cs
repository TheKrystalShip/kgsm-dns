using System.Text.Json;

namespace TheKrystalShip.KGSM.Dns.Journal;

/// <summary>
/// The events the DNS anchor records in its own journal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Transitions, not passes.</b> A zone pass runs on a timer and is quiet almost every time; each of
/// these is written when something changed, so a journal of a healthy cluster is short and every line in
/// it is a thing that happened to a name.
/// </para>
/// <para>
/// Named here rather than in the anchor so a reader deserializes against the same spelling the writer
/// used — a field spelled two ways does not fail, it arrives as null.
/// </para>
/// </remarks>
public static class DnsEvents
{
    /// <summary>A record was created for a name.</summary>
    public const string NamePublished = "dns.name.published";

    /// <summary>A name's record was corrected to what the registry says.</summary>
    public const string NameMoved = "dns.name.moved";

    /// <summary>A name's record was deleted: the name was released, or can no longer be pointed anywhere.</summary>
    public const string NameWithdrawn = "dns.name.withdrawn";

    /// <summary>A record this cluster did not publish holds a name it wants.</summary>
    public const string NameContested = "dns.name.contested";

    /// <summary>A registry row was rebuilt from the mark on its record.</summary>
    public const string NameRecovered = "dns.name.recovered";

    /// <summary>A certificate was issued for a name.</summary>
    public const string CertificateIssued = "dns.certificate.issued";

    /// <summary>An order for a certificate ended without one.</summary>
    public const string CertificateFailed = "dns.certificate.failed";

    /// <summary>A member asked for a certificate the anchor will not order for it.</summary>
    public const string CertificateRefused = "dns.certificate.refused";

    /// <summary>The zone stopped answering, after a pass that completed.</summary>
    public const string ZoneUnreachable = "dns.zone.unreachable";

    /// <summary>A pass completed again, after one that could not.</summary>
    public const string ZoneRestored = "dns.zone.restored";

    /// <summary>Somebody asked for a zone pass sooner than the timer would run one.</summary>
    public const string ZoneCheckRequested = "dns.zone.check_requested";

    /// <summary>Somebody asked for a certificate to be renewed now, ahead of its own schedule.</summary>
    public const string CertificateRenewRequested = "dns.certificate.renew_requested";

    /// <summary>Somebody named an existing game server under an additional label.</summary>
    public const string AliasAdded = "dns.alias.added";

    /// <summary>Somebody gave up an alias.</summary>
    public const string AliasRemoved = "dns.alias.removed";
}

/// <summary>
/// The bytes each <see cref="DnsEvents"/> event carries.
/// </summary>
/// <remarks>
/// The typed half of a line: what the event is about, for a reader that wants the values rather than the
/// sentence. The sentence, the weight and how it went ride the envelope beside them, written by the
/// anchor at the moment it records the fact. An absent value here is a real null rather than an empty
/// string, because "not known" and "blank" are different facts.
/// </remarks>
public static class DnsEventPayloads
{
    /// <summary>A name and where its record points.</summary>
    /// <param name="name">The fully qualified name.</param>
    /// <param name="kind"><c>member</c>, <c>capability</c> or <c>game</c>.</param>
    /// <param name="member">The member it belongs to.</param>
    /// <param name="key">The capability or instance, empty for a node's own name.</param>
    /// <param name="type">The record type.</param>
    /// <param name="content">What it points at.</param>
    /// <param name="from">What it pointed at before, on a move. Null otherwise.</param>
    public static Action<Utf8JsonWriter> Record(
        string name, string kind, string member, string key, string type, string content, string? from = null) =>
        w =>
        {
            Identity(w, name, kind, member, key);
            w.WriteString("Type", type);
            w.WriteString("Content", content);
            Nullable(w, "From", from);
        };

    /// <summary>A name leaving the zone, or held away from it.</summary>
    /// <param name="name">The fully qualified name.</param>
    /// <param name="kind">What it names, as the record's mark says.</param>
    /// <param name="member">The member it belonged to.</param>
    /// <param name="key">The capability or instance, empty for a node's own name.</param>
    /// <param name="reason">Why, in one line, or null for a name whose row was released.</param>
    public static Action<Utf8JsonWriter> Withheld(
        string name, string kind, string member, string key, string? reason) =>
        w =>
        {
            Identity(w, name, kind, member, key);
            Nullable(w, "Reason", reason);
        };

    /// <summary>A certificate for one name.</summary>
    /// <param name="name">The name it certifies.</param>
    /// <param name="member">The member it was ordered for.</param>
    /// <param name="notAfter">When the issued certificate expires, or null when none was issued.</param>
    /// <param name="reason">Why none was issued or ordered, or null when one was.</param>
    public static Action<Utf8JsonWriter> Certificate(
        string name, string member, DateTimeOffset? notAfter, string? reason) =>
        w =>
        {
            w.WriteString("Name", name);
            w.WriteString("Member", member);
            if (notAfter is { } until)
                w.WriteString("NotAfter", until);
            else
                w.WriteNull("NotAfter");
            Nullable(w, "Reason", reason);
        };

    /// <summary>The zone's standing changing.</summary>
    /// <param name="error">What the provider answered when it stopped, or null when it came back.</param>
    public static Action<Utf8JsonWriter> Zone(string? error) =>
        w => Nullable(w, "Error", error);

    /// <summary>A certificate asked for out of its own schedule.</summary>
    /// <param name="name">The name it is for.</param>
    /// <param name="member">The member it was asked for.</param>
    public static Action<Utf8JsonWriter> CertificateRequested(string name, string member) =>
        w =>
        {
            w.WriteString("Name", name);
            w.WriteString("Member", member);
        };

    /// <summary>An alias and the game server it names.</summary>
    /// <param name="name">The alias's own fully qualified name.</param>
    /// <param name="server">The game name it points at.</param>
    public static Action<Utf8JsonWriter> Alias(string name, string server) =>
        w =>
        {
            w.WriteString("Name", name);
            w.WriteString("Server", server);
        };

    private static void Identity(Utf8JsonWriter w, string name, string kind, string member, string key)
    {
        w.WriteString("Name", name);
        w.WriteString("Kind", kind);
        w.WriteString("Member", member);
        w.WriteString("Key", key);
    }

    private static void Nullable(Utf8JsonWriter w, string property, string? value)
    {
        if (string.IsNullOrEmpty(value))
            w.WriteNull(property);
        else
            w.WriteString(property, value);
    }
}
