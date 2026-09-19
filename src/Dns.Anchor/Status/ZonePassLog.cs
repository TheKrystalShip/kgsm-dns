using TheKrystalShip.KGSM.Dns.Status;

namespace TheKrystalShip.KGSM.Dns.Anchor.Status;

/// <summary>
/// What the most recent zone pass did, and when the next one is due.
/// </summary>
/// <remarks>
/// <para>
/// <b>Held in memory and nowhere else.</b> Every pass replaces what the last one said, so a restart loses
/// nothing worth keeping: the next pass says it again. The status reports null until then rather than
/// something remembered from a previous process.
/// </para>
/// <para>
/// <b>It also says when the zone's standing changed.</b> A failing pass is repeated on every tick of an
/// outage, and recording each one would bury the outage in its own echo; the caller records only the
/// pass that turned a working zone into a failing one, and the one that turned it back.
/// </para>
/// </remarks>
public sealed class ZonePassLog
{
    private readonly Lock _gate = new();
    private DnsZonePass? _last;
    private DateTimeOffset? _lastSucceededAt;
    private DateTimeOffset? _nextAt;
    private IReadOnlyList<string> _contested = [];

    /// <summary>Record a pass that completed.</summary>
    /// <returns>Whether the pass before it failed, so the zone has just come back.</returns>
    public bool Succeeded(DateTimeOffset startedAt, TimeSpan took, RecordReconciler.Pass pass)
    {
        lock (_gate)
        {
            bool restored = _last is { Succeeded: false };
            _last = new DnsZonePass(
                startedAt, Milliseconds(took), Succeeded: true, Error: null,
                pass.Published, pass.Moved, pass.Removed, pass.Recovered);
            _lastSucceededAt = startedAt + took;
            _contested = pass.Contested;
            return restored;
        }
    }

    /// <summary>Record a pass that could not complete.</summary>
    /// <returns>Whether this is the first failure after a pass that completed, or after none at all.</returns>
    public bool Failed(DateTimeOffset startedAt, TimeSpan took, string error)
    {
        lock (_gate)
        {
            bool began = _last is not { Succeeded: false };
            _last = new DnsZonePass(startedAt, Milliseconds(took), Succeeded: false, error, 0, 0, 0, 0);
            return began;
        }
    }

    /// <summary>Say when the next pass is due, or that this member runs none.</summary>
    public void Next(DateTimeOffset? at)
    {
        lock (_gate)
            _nextAt = at;
    }

    /// <summary>The zone's standing as of now.</summary>
    public DnsZoneStatus Snapshot(bool read)
    {
        lock (_gate)
            return new DnsZoneStatus(read, _last, _lastSucceededAt, _nextAt, _contested);
    }

    private static int Milliseconds(TimeSpan took) => (int)Math.Clamp(took.TotalMilliseconds, 0, int.MaxValue);
}
