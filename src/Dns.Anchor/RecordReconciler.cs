using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Dns;
using TheKrystalShip.KGSM.Dns.Anchor.Providers;
using TheKrystalShip.KGSM.Dns.Anchor.Registry;

namespace TheKrystalShip.KGSM.Dns.Anchor;

/// <summary>
/// Brings the zone to what the registry says, and recovers the registry from the zone when there is
/// nothing to bring.
/// </summary>
/// <remarks>
/// <para>
/// <b>One direction, always.</b> The registry is the desired state and the records are reconciled to
/// it, so a record edited by hand is drift that heals on the next pass rather than a change that wins.
/// The exception is the provider's own handle for a record, which only the provider can say.
/// </para>
/// <para>
/// <b>A record without this cluster's mark is never touched.</b> Not edited, not deleted, not counted
/// as satisfying a name. A zone holds records an operator made, and the mark is the only honest way to
/// tell one of those from one of these — the name alone is not evidence, because a person is free to
/// make a record that looks exactly like one of ours.
/// </para>
/// <para>
/// <b>Reconciling is how recovery happens too.</b> Every row is derivable from the marks, so a member
/// that takes the capability with an empty registry rebuilds it from the records themselves before it
/// changes anything. Nothing has to move between the old holder and the new one.
/// </para>
/// <para>
/// <b>Recovery happens once, before anything is claimed, and only when the registry is empty.</b>
/// Once this member has read its registry, an empty one means every name was released — which looks
/// identical to an unbuilt one and must not be answered the same way, or a name given up would be
/// rebuilt from the record it is about to withdraw and never go away. And it precedes every claim,
/// because a claim against an unread registry draws a new name for a member the zone already names.
/// </para>
/// </remarks>
public sealed class RecordReconciler(
    SqliteNameRegistry registry,
    IDnsProvider provider,
    ClusterBases bases,
    int ttlSeconds,
    ILogger<RecordReconciler> logger)
{
    /// <summary>What one pass did, for the caller to log, record or report.</summary>
    /// <param name="Changes">Every change the pass made to the zone or learned from it, in the order made.</param>
    /// <param name="Contested">
    /// Every name a record this cluster does not own is holding, as the pass found the zone — those it
    /// found this time and those it found before and that are still held.
    /// </param>
    public sealed record Pass(IReadOnlyList<ZoneChange> Changes, IReadOnlyList<string> Contested)
    {
        /// <summary>Records created.</summary>
        public int Published => Count(ZoneChangeKind.Published);

        /// <summary>Records whose content, TTL or proxying was corrected.</summary>
        public int Moved => Count(ZoneChangeKind.Moved);

        /// <summary>Records deleted because no row wants them, or their row can point nowhere.</summary>
        public int Removed => Count(ZoneChangeKind.Withdrawn);

        /// <summary>Rows rebuilt from marks.</summary>
        public int Recovered => Count(ZoneChangeKind.Recovered);

        /// <summary>Names this pass found held by a record this cluster does not own.</summary>
        public int NewlyContested => Count(ZoneChangeKind.Contested);

        /// <summary>Whether anything at all happened, which is what decides if a pass is worth a line.</summary>
        public bool Quiet => Published == 0 && Moved == 0 && Removed == 0 && Recovered == 0;

        private int Count(ZoneChangeKind kind) => Changes.Count(c => c.Kind == kind);
    }

    private bool _read;

    // Changes made and not yet handed to a caller: a recovery, reported by the first pass after it, and
    // whatever a pass that failed partway had already done to the zone before it failed.
    private List<ZoneChange> _unreported = [];

    /// <summary>
    /// Take the changes a failed pass made before it failed. The zone holds them whether or not the pass
    /// finished, so they are as real as any a completed pass reports.
    /// </summary>
    public IReadOnlyList<ZoneChange> TakeUnreported()
    {
        List<ZoneChange> taken = _unreported;
        _unreported = [];
        return taken;
    }

    /// <summary>Whether this member has read its registry against the zone yet.</summary>
    public bool Ready => _read;

    /// <summary>
    /// Read the registry against the zone once, rebuilding it from the marks when it is empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called before anything claims a name. A claim against an unread registry draws a fresh name for
    /// a member that already holds one in the zone — and the next pass then withdraws the name that
    /// member was reached at, which is the exact loss the marks exist to prevent.
    /// </para>
    /// <para>
    /// A failed read leaves the member unready, so the caller claims nothing and asks again on the next
    /// pass. "Could not read the zone" is not "the zone holds nothing".
    /// </para>
    /// </remarks>
    public async Task EnsureReadyAsync(CancellationToken ct)
    {
        if (_read)
            return;

        IReadOnlyList<ProviderRecord> zone = await provider.ListAsync(ct).ConfigureAwait(false);

        if (registry.All().Count == 0)
            _unreported.AddRange(Recover(Ours(zone)));

        _read = true;
    }

    /// <summary>Run one pass.</summary>
    public async Task<Pass> RunAsync(CancellationToken ct)
    {
        await EnsureReadyAsync(ct).ConfigureAwait(false);

        IReadOnlyList<ProviderRecord> zone = await provider.ListAsync(ct).ConfigureAwait(false);
        List<ProviderRecord> ours = Ours(zone);

        // Held until this pass returns, so a recovery appears in the same report as everything else the
        // pass did, and a failure below leaves what was already done for TakeUnreported.
        List<ZoneChange> changes = _unreported;

        var byName = new Dictionary<string, ProviderRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (ProviderRecord record in zone)
            byName.TryAdd(record.Name, record);

        var contested = new List<string>();

        foreach (NameRow row in registry.All())
        {
            ct.ThrowIfCancellationRequested();

            // A row with nothing to point at is a state, not a record: a member whose address nobody has
            // observed, or a name whose whole numbered series is held. Publishing a guess would be worse
            // than publishing nothing. A record this cluster published for the name before it was blocked
            // is withdrawn with it: a capability that moved to a holder nobody has heard from yet would
            // otherwise keep resolving to the member that no longer holds it.
            byName.TryGetValue(row.Name, out ProviderRecord? held);

            if (row.State == NameState.Blocked || string.IsNullOrEmpty(row.Content))
            {
                if (held?.Mark is not null)
                {
                    await provider.DeleteAsync(held.Id, ct).ConfigureAwait(false);
                    registry.Put(row with { RecordId = "", UpdatedAt = DateTimeOffset.UtcNow });
                    logger.LogInformation("withdrew {Name}: {Note}", row.Name, row.Note);
                    changes.Add(ZoneChange.Of(ZoneChangeKind.Withdrawn, row, reason: row.Note));
                }
                else if (held is not null)
                {
                    // A contested row stays blocked and is not tried again, so it is found here on every
                    // pass after the one that contested it — which is what keeps it on the list for as
                    // long as the record that holds the name is there.
                    contested.Add(row.Name);
                }

                continue;
            }

            if (held is null)
            {
                try
                {
                    ProviderRecord created = await provider
                        .CreateAsync(Desired(row), ct).ConfigureAwait(false);
                    registry.Put(row with
                    {
                        RecordId = created.Id,
                        State = NameState.Published,
                        Note = "",
                        UpdatedAt = DateTimeOffset.UtcNow,
                    });
                    changes.Add(ZoneChange.Of(ZoneChangeKind.Published, row));
                }
                catch (DnsNameTakenException ex)
                {
                    // The provider refuses a name another record already holds, and that refusal is what
                    // keeps two claims from both succeeding. The listing above did not show it, so the
                    // record appeared between the two calls.
                    Contest(row, ex.Message);
                    changes.Add(ZoneChange.Of(ZoneChangeKind.Contested, row, reason: ex.Message));
                    contested.Add(row.Name);
                }

                continue;
            }

            if (held.Mark is null)
            {
                const string note = "a record this cluster did not publish holds this name";
                Contest(row, note);
                changes.Add(ZoneChange.Of(ZoneChangeKind.Contested, row, reason: note));
                contested.Add(row.Name);
                continue;
            }

            bool drifted =
                !string.Equals(held.Content, row.Content, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(held.Type, row.Type, StringComparison.OrdinalIgnoreCase)
                || held.Proxied
                || held.Ttl != ttlSeconds
                || !string.Equals(held.Comment, row.Mark.ToString(), StringComparison.Ordinal);

            if (drifted)
            {
                ProviderRecord updated = await provider
                    .UpdateAsync(held.Id, Desired(row), ct).ConfigureAwait(false);
                registry.Put(row with
                {
                    RecordId = updated.Id,
                    State = NameState.Published,
                    Note = "",
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
                changes.Add(ZoneChange.Of(ZoneChangeKind.Moved, row, from: held.Content));
            }
            else if (row.State != NameState.Published || row.RecordId != held.Id)
            {
                registry.Put(row with
                {
                    RecordId = held.Id,
                    State = NameState.Published,
                    Note = "",
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            }
        }

        // A marked record no row wants is one whose name was released. Marked, so it is this cluster's
        // to remove; unmarked records are outside this loop entirely.
        foreach (ProviderRecord record in ours)
        {
            ct.ThrowIfCancellationRequested();

            if (registry.Find(record.Name) is not null)
                continue;

            await provider.DeleteAsync(record.Id, ct).ConfigureAwait(false);
            logger.LogInformation("withdrew {Name}", record.Name);

            // The row is gone, so what the name was is read off the mark the record carried.
            RecordMark mark = record.Mark!.Value;
            changes.Add(new ZoneChange(
                ZoneChangeKind.Withdrawn, record.Name, mark.Kind, mark.Member, mark.Key,
                record.Type, record.Content, From: null, Reason: null));
        }

        _unreported = [];
        return new Pass(changes, contested);
    }

    /// <summary>
    /// Rebuild rows from the marks, for a member that starts with an empty registry.
    /// </summary>
    /// <remarks>
    /// Recovery is additive and never the other way round: a record is only ever read into a row the
    /// registry does not have. A row that exists stays authoritative, so a stale record can never
    /// overwrite what the cluster currently wants.
    /// </remarks>
    private List<ZoneChange> Recover(IReadOnlyList<ProviderRecord> ours)
    {
        var recovered = new List<ZoneChange>();

        foreach (ProviderRecord record in ours)
        {
            if (record.Mark is not { } mark || registry.Find(record.Name) is not null)
                continue;

            var row = new NameRow(
                Name: record.Name,
                Kind: mark.Kind,
                Member: mark.Member,
                Key: mark.Key,
                Type: record.Type,
                Content: record.Content,
                State: NameState.Published,
                RecordId: record.Id,
                Note: "",
                UpdatedAt: DateTimeOffset.UtcNow);
            registry.Put(row);

            logger.LogInformation(
                "recovered {Name} from the zone — {Member} holds it", record.Name, mark.Member);
            recovered.Add(ZoneChange.Of(ZoneChangeKind.Recovered, row));
        }

        return recovered;
    }

    /// <summary>
    /// Record that something else holds a name this cluster wants.
    /// </summary>
    /// <remarks>
    /// Written as a blocked row rather than retried: the caller that chose the label is the only thing
    /// that can choose another, and a reconcile loop trying the same name every pass would hide the
    /// collision behind noise instead of surfacing it.
    /// </remarks>
    private void Contest(NameRow row, string note)
    {
        logger.LogWarning("{Name} is held by a record this cluster does not own", row.Name);
        registry.Put(row with
        {
            State = NameState.Blocked,
            Note = note,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>
    /// The records that are this cluster's: inside one of its bases and carrying its mark. A record
    /// elsewhere in the zone is not even a candidate for being read as one of ours.
    /// </summary>
    private List<ProviderRecord> Ours(IReadOnlyList<ProviderRecord> zone) =>
        [.. zone.Where(r => bases.Owns(r.Name) && r.Mark is not null)];

    private DesiredRecord Desired(NameRow row) =>
        new(row.Name, row.Type, row.Content, ttlSeconds, row.Mark.ToString());
}

/// <summary>What a zone pass did to one name.</summary>
public enum ZoneChangeKind
{
    /// <summary>A record was created.</summary>
    Published,

    /// <summary>A record was corrected to what the registry says.</summary>
    Moved,

    /// <summary>A record was deleted.</summary>
    Withdrawn,

    /// <summary>A record this cluster does not own was found holding the name.</summary>
    Contested,

    /// <summary>A registry row was rebuilt from the record's mark.</summary>
    Recovered,
}

/// <summary>One change a zone pass made, or learned from the zone.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Name">The name it happened to.</param>
/// <param name="NameKind">What the name names.</param>
/// <param name="Member">The member it belongs, or belonged, to.</param>
/// <param name="Key">The capability or instance, empty for a node's own name.</param>
/// <param name="Type">The record type.</param>
/// <param name="Content">What the record points at, or pointed at when it was withdrawn.</param>
/// <param name="From">What a moved record pointed at before. Null otherwise.</param>
/// <param name="Reason">Why a name was withdrawn or contested, when there is a reason to give.</param>
public sealed record ZoneChange(
    ZoneChangeKind Kind,
    string Name,
    NameKind NameKind,
    string Member,
    string Key,
    string Type,
    string Content,
    string? From,
    string? Reason)
{
    /// <summary>A change to the name one row holds.</summary>
    public static ZoneChange Of(ZoneChangeKind kind, NameRow row, string? from = null, string? reason = null) =>
        new(kind, row.Name, row.Kind, row.Member, row.Key, row.Type, row.Content, from,
            string.IsNullOrEmpty(reason) ? null : reason);
}
