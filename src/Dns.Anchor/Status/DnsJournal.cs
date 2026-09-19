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

    /// <summary>The surface an admin action came through.</summary>
    internal const string OriginUi = "ui";

    /// <summary>Record every change one zone pass made.</summary>
    public async Task PassAsync(RecordReconciler.Pass pass, CancellationToken ct)
    {
        foreach (ZoneChange change in pass.Changes)
            await ChangeAsync(change, ct).ConfigureAwait(false);
    }

    /// <summary>Record the zone stopping answering, or starting again.</summary>
    public Task ZoneAsync(bool reachable, string? error, CancellationToken ct) =>
        WriteAsync(reachable ? DnsEvents.ZoneRestored : DnsEvents.ZoneUnreachable,
            DnsEventPayloads.Zone(reachable ? null : error), ct);

    /// <summary>Record a certificate being issued.</summary>
    public Task IssuedAsync(string name, string member, DateTimeOffset? notAfter, CancellationToken ct) =>
        WriteAsync(DnsEvents.CertificateIssued, DnsEventPayloads.Certificate(name, member, notAfter, null), ct);

    /// <summary>Record an order ending without a certificate.</summary>
    public Task FailedAsync(string name, string member, string reason, CancellationToken ct) =>
        WriteAsync(DnsEvents.CertificateFailed, DnsEventPayloads.Certificate(name, member, null, reason), ct);

    /// <summary>Record a member being refused a certificate.</summary>
    /// <remarks>
    /// The actor is the member that asked. Nothing a person did led here, and naming this anchor would
    /// hide which member asked for a name it does not hold.
    /// </remarks>
    public Task RefusedAsync(string name, string member, string reason, CancellationToken ct) =>
        WriteAsync(DnsEvents.CertificateRefused, DnsEventPayloads.Certificate(name, member, null, reason), ct,
            actor: "member:" + member);

    /// <summary>Record an admin asking for a zone pass sooner than the timer would run one.</summary>
    public Task ZoneCheckRequestedAsync(string actor, CancellationToken ct) =>
        WriteAsync(DnsEvents.ZoneCheckRequested, _ => { }, ct, actor, OriginUi);

    /// <summary>Record an admin asking for a certificate to be renewed now, ahead of its own schedule.</summary>
    public Task RenewRequestedAsync(string name, string member, string actor, CancellationToken ct) =>
        WriteAsync(DnsEvents.CertificateRenewRequested, DnsEventPayloads.CertificateRequested(name, member), ct,
            actor, OriginUi);

    /// <summary>Record an admin naming an existing game server under an additional label.</summary>
    public Task AliasAddedAsync(string name, string server, string actor, CancellationToken ct) =>
        WriteAsync(DnsEvents.AliasAdded, DnsEventPayloads.Alias(name, server), ct, actor, OriginUi);

    /// <summary>Record an admin giving up an alias.</summary>
    public Task AliasRemovedAsync(string name, string server, string actor, CancellationToken ct) =>
        WriteAsync(DnsEvents.AliasRemoved, DnsEventPayloads.Alias(name, server), ct, actor, OriginUi);

    private Task ChangeAsync(ZoneChange change, CancellationToken ct)
    {
        string kind = NameKinds.Spell(change.NameKind);
        return change.Kind switch
        {
            ZoneChangeKind.Published => WriteAsync(DnsEvents.NamePublished,
                DnsEventPayloads.Record(change.Name, kind, change.Member, change.Key, change.Type, change.Content), ct),
            ZoneChangeKind.Moved => WriteAsync(DnsEvents.NameMoved,
                DnsEventPayloads.Record(change.Name, kind, change.Member, change.Key, change.Type, change.Content,
                    change.From), ct),
            ZoneChangeKind.Recovered => WriteAsync(DnsEvents.NameRecovered,
                DnsEventPayloads.Record(change.Name, kind, change.Member, change.Key, change.Type, change.Content), ct),
            ZoneChangeKind.Withdrawn => WriteAsync(DnsEvents.NameWithdrawn,
                DnsEventPayloads.Withheld(change.Name, kind, change.Member, change.Key, change.Reason), ct),
            _ => WriteAsync(DnsEvents.NameContested,
                DnsEventPayloads.Withheld(change.Name, kind, change.Member, change.Key, change.Reason), ct),
        };
    }

    private Task WriteAsync(
        string type, Action<Utf8JsonWriter> payload, CancellationToken ct, string? actor = null, string? origin = null) =>
        RecordAsync(EventName.Parse(type), payload, actor, origin, ct: ct);
}
