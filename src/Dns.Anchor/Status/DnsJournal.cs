using System.Globalization;
using System.Text.Json;

using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Dns.Journal;
using TheKrystalShip.KGSM.Events;
using TheKrystalShip.KGSM.Services;

namespace TheKrystalShip.KGSM.Dns.Anchor.Status;

/// <summary>
/// Records what happened to this cluster's names and certificates, in this anchor's own event journal.
/// </summary>
/// <remarks>
/// <para>
/// <b>A producer records what that producer did.</b> Only the holder of the <c>dns</c> capability writes
/// the zone and orders certificates, so nothing else witnesses either. The lines sit beside every other
/// producer's journal, where a Control Panel on the same machine merges them into its audit, and this
/// anchor serves the newest of them itself for a cluster with no panel beside it.
/// </para>
/// <para>
/// <b>The shapes are the package's.</b> Names and payloads come from <see cref="DnsEvents"/> and
/// <see cref="DnsEventPayloads"/>, so a reader and this writer cannot spell a field two ways.
/// </para>
/// <para>
/// <b>Every line says what happened and how much it matters.</b> The sentence is written here, at the
/// moment the fact is recorded, and the weight is stamped beside it — so a name freezes into the line as
/// it was spelled then, and every surface reading the journal reports the same thing without composing a
/// sentence of its own.
/// </para>
/// <para>
/// <b>Best-effort.</b> The change has already been made when one of these is called; a line that could not
/// be written is logged by the base and never undoes it.
/// </para>
/// </remarks>
internal sealed class DnsJournal(IEventJournalWriter writer, ILogger<DnsJournal> logger)
    : JournalRecorder(writer, logger)
{
    /// <summary>
    /// This anchor's producer id — its state directory's name, which is where a reader looks for its
    /// journal. A journal written anywhere else is not reported as misplaced; it is simply never found.
    /// </summary>
    internal const string ProducerId = "kgsm-dns";

    /// <summary>The surface a person's action came through.</summary>
    internal const string OriginUi = "ui";

    /// <summary>Record every change one zone pass made.</summary>
    public async Task PassAsync(RecordReconciler.Pass pass, CancellationToken ct)
    {
        foreach (ZoneChange change in pass.Changes)
            await ChangeAsync(change, ct).ConfigureAwait(false);
    }

    /// <summary>Record the zone stopping answering, or starting again.</summary>
    /// <remarks>
    /// A zone that will not answer is the whole capability down: no name can be published, moved or
    /// withdrawn while it lasts, and no certificate can be ordered behind it. Nothing else this anchor
    /// records is worth as much.
    /// </remarks>
    public Task ZoneAsync(bool reachable, string? error, CancellationToken ct) =>
        reachable
            ? WriteAsync(DnsEvents.ZoneRestored, DnsEventPayloads.Zone(null), ct,
                severity: EventSeverity.Info, outcome: EventOutcome.Success,
                summary: "the zone is answering again")
            : WriteAsync(DnsEvents.ZoneUnreachable, DnsEventPayloads.Zone(error), ct,
                severity: EventSeverity.Danger, outcome: EventOutcome.Failure,
                summary: "the zone stopped answering" + Because(error));

    /// <summary>Record a certificate being issued.</summary>
    public Task IssuedAsync(string name, string member, DateTimeOffset? notAfter, CancellationToken ct) =>
        WriteAsync(DnsEvents.CertificateIssued, DnsEventPayloads.Certificate(name, member, notAfter, null), ct,
            severity: EventSeverity.Info, outcome: EventOutcome.Success,
            summary: notAfter is { } until
                ? $"issued a certificate for {name}, valid until {Day(until)}"
                : $"issued a certificate for {name}");

    /// <summary>Record an order ending without a certificate.</summary>
    /// <remarks>
    /// <b>Warn, not danger.</b> The member asks again, and the name keeps the certificate it already
    /// holds until that one expires — so one failed order is a condition worth watching rather than
    /// something that has broken. A run of them is what a reader is looking for, and the weight is what
    /// makes the run legible beside the ordinary issuance either side of it.
    /// </remarks>
    public Task FailedAsync(string name, string member, string reason, CancellationToken ct) =>
        WriteAsync(DnsEvents.CertificateFailed, DnsEventPayloads.Certificate(name, member, null, reason), ct,
            severity: EventSeverity.Warn, outcome: EventOutcome.Failure,
            summary: $"could not get a certificate for {name}{Because(reason)}");

    /// <summary>Record a member being refused a certificate.</summary>
    /// <remarks>
    /// The actor is the member that asked. Nothing a person did led here, and naming this anchor would
    /// hide which member asked for a name it does not hold.
    /// </remarks>
    public Task RefusedAsync(string name, string member, string reason, CancellationToken ct) =>
        WriteAsync(DnsEvents.CertificateRefused, DnsEventPayloads.Certificate(name, member, null, reason), ct,
            actor: "member:" + member,
            severity: EventSeverity.Warn, outcome: EventOutcome.Failure,
            summary: $"asked for a certificate for {name} and was refused{Because(reason)}");

    /// <summary>Record somebody asking for a zone pass sooner than the timer would run one.</summary>
    public Task ZoneCheckRequestedAsync(string actor, CancellationToken ct) =>
        WriteAsync(DnsEvents.ZoneCheckRequested, _ => { }, ct, actor, OriginUi,
            severity: EventSeverity.Info, outcome: EventOutcome.Neutral,
            summary: "asked for a zone pass now");

    /// <summary>Record somebody asking for a certificate to be renewed now, ahead of its own schedule.</summary>
    public Task RenewRequestedAsync(string name, string member, string actor, CancellationToken ct) =>
        WriteAsync(DnsEvents.CertificateRenewRequested, DnsEventPayloads.CertificateRequested(name, member), ct,
            actor, OriginUi,
            severity: EventSeverity.Info, outcome: EventOutcome.Neutral,
            summary: $"asked for {name}'s certificate to be renewed now");

    /// <summary>Record somebody naming an existing game server under an additional label.</summary>
    public Task AliasAddedAsync(string name, string server, string actor, CancellationToken ct) =>
        WriteAsync(DnsEvents.AliasAdded, DnsEventPayloads.Alias(name, server), ct, actor, OriginUi,
            severity: EventSeverity.Info, outcome: EventOutcome.Success,
            summary: $"named {server} {name} as well");

    /// <summary>Record somebody giving up an alias.</summary>
    /// <remarks>
    /// <b>Warn, like every other name leaving the zone.</b> An alias is what somebody's bookmark and
    /// whatever they told their players both say, and giving it up stops all of them resolving.
    /// </remarks>
    public Task AliasRemovedAsync(string name, string server, string actor, CancellationToken ct) =>
        WriteAsync(DnsEvents.AliasRemoved, DnsEventPayloads.Alias(name, server), ct, actor, OriginUi,
            severity: EventSeverity.Warn, outcome: EventOutcome.Neutral,
            summary: $"gave up {name}, an alias for {server}");

    /// <summary>
    /// Record one change a zone pass made.
    /// </summary>
    /// <remarks>
    /// <b>Weight follows what the change does to whoever uses the name.</b> Publishing and correcting a
    /// record leave a name reachable, so they are the routine good news the pass exists for. A name
    /// leaving the zone stops resolving for everyone holding it, and a name another record is sitting on
    /// cannot be published at all — those two are what somebody reading the feed is looking for.
    /// </remarks>
    private Task ChangeAsync(ZoneChange change, CancellationToken ct)
    {
        string kind = NameKinds.Spell(change.NameKind);
        string word = Word(change.NameKind);
        string what = word.Length == 0 ? $"the name {change.Name}" : $"the {word} name {change.Name}";

        return change.Kind switch
        {
            ZoneChangeKind.Published => WriteAsync(DnsEvents.NamePublished,
                DnsEventPayloads.Record(change.Name, kind, change.Member, change.Key, change.Type, change.Content), ct,
                severity: EventSeverity.Info, outcome: EventOutcome.Success,
                summary: $"published {what}, pointing at {change.Content}"),

            ZoneChangeKind.Moved => WriteAsync(DnsEvents.NameMoved,
                DnsEventPayloads.Record(change.Name, kind, change.Member, change.Key, change.Type, change.Content,
                    change.From), ct,
                severity: EventSeverity.Info, outcome: EventOutcome.Success,
                summary: string.IsNullOrEmpty(change.From)
                    ? $"pointed {what} at {change.Content}"
                    : $"moved {what} from {change.From} to {change.Content}"),

            ZoneChangeKind.Recovered => WriteAsync(DnsEvents.NameRecovered,
                DnsEventPayloads.Record(change.Name, kind, change.Member, change.Key, change.Type, change.Content), ct,
                severity: EventSeverity.Info, outcome: EventOutcome.Success,
                summary: $"rebuilt {change.Member}'s claim on {what} from the record holding it"),

            ZoneChangeKind.Withdrawn => WriteAsync(DnsEvents.NameWithdrawn,
                DnsEventPayloads.Withheld(change.Name, kind, change.Member, change.Key, change.Reason), ct,
                severity: EventSeverity.Warn, outcome: EventOutcome.Neutral,
                summary: $"withdrew {what}{Because(change.Reason)}"),

            _ => WriteAsync(DnsEvents.NameContested,
                DnsEventPayloads.Withheld(change.Name, kind, change.Member, change.Key, change.Reason), ct,
                severity: EventSeverity.Warn, outcome: EventOutcome.Failure,
                summary: $"could not publish {what} — a record this cluster did not write holds it"
                    + (string.IsNullOrEmpty(change.Reason) ? "" : $" ({change.Reason})")),
        };
    }

    /// <summary>
    /// What a name of this kind is called in a sentence.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="NameKinds.Spell"/>, which is the wire spelling a record's mark carries and
    /// must not move. This is prose, and a reader of the audit feed has no reason to know that a node's
    /// own name is stored under the word "member".
    /// </remarks>
    private static string Word(NameKind kind) => kind switch
    {
        NameKind.Member => "node",
        NameKind.Capability => "capability",
        NameKind.Game => "game",
        NameKind.Alias => "alias",
        NameKind.CapabilityAlias => "capability alias",
        _ => "",
    };

    /// <summary>A reason clause, or nothing at all when there is no reason to give.</summary>
    private static string Because(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? "" : " — " + reason;

    /// <summary>A date somebody reads rather than parses. The anchor runs invariant, so this is stable.</summary>
    private static string Day(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    private Task WriteAsync(
        string type, Action<Utf8JsonWriter> payload, CancellationToken ct, string? actor = null, string? origin = null,
        EventSeverity? severity = null, EventOutcome? outcome = null, string? summary = null) =>
        RecordAsync(EventName.Parse(type), payload, actor, origin, severity, outcome, summary, ct);
}
