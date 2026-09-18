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
    /// <summary>What one pass did, for the caller to log or report.</summary>
    /// <param name="Published">Records created.</param>
    /// <param name="Moved">Records whose content, TTL or proxying was corrected.</param>
    /// <param name="Removed">Records deleted because no row wants them.</param>
    /// <param name="Recovered">Rows rebuilt from marks.</param>
    /// <param name="Contested">Names a record this cluster does not own is holding.</param>
    public readonly record struct Pass(int Published, int Moved, int Removed, int Recovered, int Contested)
    {
        /// <summary>Whether anything at all happened, which is what decides if a pass is worth a line.</summary>
        public bool Quiet => Published == 0 && Moved == 0 && Removed == 0 && Recovered == 0;
    }

    private bool _read;
    private int _recovered;

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
            _recovered = Recover(Ours(zone));

        _read = true;
    }

    /// <summary>Run one pass.</summary>
    public async Task<Pass> RunAsync(CancellationToken ct)
    {
        await EnsureReadyAsync(ct).ConfigureAwait(false);

        IReadOnlyList<ProviderRecord> zone = await provider.ListAsync(ct).ConfigureAwait(false);
        List<ProviderRecord> ours = Ours(zone);

        // Reported on the first pass after it happened, so a recovery appears in the same line as
        // everything else a pass did.
        int recovered = _recovered;
        _recovered = 0;

        var byName = new Dictionary<string, ProviderRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (ProviderRecord record in zone)
            byName.TryAdd(record.Name, record);

        int published = 0, moved = 0, removed = 0, contested = 0;

        foreach (NameRow row in registry.All())
        {
            ct.ThrowIfCancellationRequested();

            // A row with nothing to point at is a state, not a record: a member whose address nobody has
            // observed, or a name whose whole numbered series is held. Publishing a guess would be worse
            // than publishing nothing.
            if (row.State == NameState.Blocked || string.IsNullOrEmpty(row.Content))
                continue;

            byName.TryGetValue(row.Name, out ProviderRecord? held);

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
                    published++;
                }
                catch (DnsNameTakenException ex)
                {
                    // The provider refuses a name another record already holds, and that refusal is what
                    // keeps two claims from both succeeding. The listing above did not show it, so the
                    // record appeared between the two calls.
                    Contest(row, ex.Message);
                    contested++;
                }

                continue;
            }

            if (held.Mark is null)
            {
                Contest(row, "a record this cluster did not publish holds this name");
                contested++;
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
                moved++;
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
            removed++;
        }

        return new Pass(published, moved, removed, recovered, contested);
    }

    /// <summary>
    /// Rebuild rows from the marks, for a member that starts with an empty registry.
    /// </summary>
    /// <remarks>
    /// Recovery is additive and never the other way round: a record is only ever read into a row the
    /// registry does not have. A row that exists stays authoritative, so a stale record can never
    /// overwrite what the cluster currently wants.
    /// </remarks>
    private int Recover(IReadOnlyList<ProviderRecord> ours)
    {
        int recovered = 0;

        foreach (ProviderRecord record in ours)
        {
            if (record.Mark is not { } mark || registry.Find(record.Name) is not null)
                continue;

            registry.Put(new NameRow(
                Name: record.Name,
                Kind: mark.Kind,
                Member: mark.Member,
                Key: mark.Key,
                Type: record.Type,
                Content: record.Content,
                State: NameState.Published,
                RecordId: record.Id,
                Note: "",
                UpdatedAt: DateTimeOffset.UtcNow));

            logger.LogInformation(
                "recovered {Name} from the zone — {Member} holds it", record.Name, mark.Member);
            recovered++;
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
