using System.Collections.Concurrent;
using System.Net;

using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Dns;
using TheKrystalShip.KGSM.Dns.Anchor.Providers;
using TheKrystalShip.KGSM.Dns.Anchor.Registry;

namespace TheKrystalShip.KGSM.Dns.Anchor.Acme;

/// <summary>
/// Answers a DNS-01 challenge by writing its TXT record through the provider, and waits until the
/// zone's own servers carry it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only for a name inside the cluster's bases, or a capability alias the registry holds.</b> The
/// challenge record sits one label deeper than the name, which puts it outside every base by the
/// containment rule, so containment is checked against the name being validated before anything is
/// written for it.
/// </para>
/// <para>
/// The record carries a comment that is not a mark, so the zone reconcile never counts it as one of the
/// cluster's names, and it is removed as soon as the issuer has looked.
/// </para>
/// </remarks>
public sealed class ZoneDns01Solver(
    IDnsProvider provider,
    ClusterBases bases,
    SqliteNameRegistry registry,
    ILogger<ZoneDns01Solver> logger) : IDns01Solver
{
    private readonly ConcurrentDictionary<string, string> _written = new(StringComparer.OrdinalIgnoreCase);

    public async Task PresentAsync(string name, string value, CancellationToken ct)
    {
        // A capability alias sits at the top of the zone, beside names the operator made, so the shape of
        // the name is not enough there: it has to be one the registry holds as an alias.
        bool alias = bases.HoldsAlias(name) && registry.Find(name) is { Kind: NameKind.CapabilityAlias };
        if (!bases.Owns(name) && !alias)
            throw new AcmeException($"{name} is not one of this cluster's names");

        string challenge = ClusterBases.ChallengeFor(name);

        ProviderRecord record = await provider.CreateAsync(
            new DesiredRecord(challenge, "TXT", value, 60, "kgsm acme challenge"), ct).ConfigureAwait(false);
        _written[name] = record.Id;

        IReadOnlyList<string> servers = await provider.NameServersAsync(ct).ConfigureAwait(false);
        var addresses = new List<IPAddress>();
        foreach (string server in servers)
        {
            IPAddress[] resolved = await System.Net.Dns.GetHostAddressesAsync(server, ct).ConfigureAwait(false);
            addresses.AddRange(resolved.Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork));
        }

        if (addresses.Count == 0)
            throw new AcmeException("the zone's nameservers could not be resolved");

        // Every one of the zone's servers has to carry the value, because the issuer may ask any of them.
        DateTimeOffset giveUp = DateTimeOffset.UtcNow.AddMinutes(3);
        while (true)
        {
            int carrying = 0;
            foreach (IPAddress address in addresses)
            {
                try
                {
                    IReadOnlyList<string> values = await TxtProbe
                        .QueryAsync(address, challenge, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                    if (values.Contains(value, StringComparer.Ordinal))
                        carrying++;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // One unanswered question is a server that did not answer this time, not a verdict.
                }
            }

            if (carrying == addresses.Count)
            {
                logger.LogInformation("{Challenge} is visible at every nameserver of the zone", challenge);
                return;
            }

            if (DateTimeOffset.UtcNow > giveUp)
                throw new AcmeException($"{challenge} did not reach every nameserver of the zone within three minutes");

            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
    }

    public async Task CleanupAsync(string name, CancellationToken ct)
    {
        if (_written.TryRemove(name, out string? id))
            await provider.DeleteAsync(id, ct).ConfigureAwait(false);
    }
}
