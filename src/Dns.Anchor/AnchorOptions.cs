using TheKrystalShip.KGSM.Dns;

namespace TheKrystalShip.KGSM.Dns.Anchor;

/// <summary>
/// The validated form of <see cref="AnchorSettings"/>: what the daemon runs on, with every value
/// clamped to something it can honour.
/// </summary>
/// <remarks>
/// Separate from the settings type so the raw configuration and the runtime view stay distinguishable.
/// A value below its floor is raised rather than refused — a daemon that will not start because a
/// number is one below a bound is worse than one that starts and says what it used.
/// </remarks>
/// <param name="MemberId">This anchor's identity as a cluster member.</param>
/// <param name="ListenAddress">Where Kestrel binds.</param>
/// <param name="PublicBaseUrl">Where other members reach it, when it cannot see its own address.</param>
/// <param name="PublicAddress">This machine's public address, which nothing else can observe for it.</param>
/// <param name="Bases">The zone and the three bases names live under.</param>
/// <param name="MemberNames">The names members are drawn from, and the pins that override a draw.</param>
/// <param name="Provider">Which provider implementation acts on the zone.</param>
/// <param name="ProviderZoneId">The provider's own id for the zone.</param>
/// <param name="ProviderToken">The provider credential.</param>
/// <param name="Ttl">How long resolvers may cache a name.</param>
/// <param name="Reconcile">How often the zone is compared with the registry.</param>
/// <param name="RegistryPath">Where claimed names are recorded.</param>
/// <param name="ClusterStorePath">Where this member keeps its roster, outbox and inbox.</param>
/// <param name="ConfigDescriptorPath">Where the descriptor this build wrote is installed.</param>
internal sealed record AnchorOptions(
    string MemberId,
    string ListenAddress,
    string PublicBaseUrl,
    string PublicAddress,
    ClusterBases Bases,
    Names.MemberNamePool MemberNames,
    string Provider,
    string ProviderZoneId,
    string ProviderToken,
    int Ttl,
    TimeSpan Reconcile,
    string RegistryPath,
    string ClusterStorePath,
    string ConfigDescriptorPath)
{
    /// <summary>Whether a zone and a credential are configured well enough to publish anything.</summary>
    /// <remarks>
    /// An anchor missing either is not broken: it holds the capability, says it is unconfigured, and
    /// writes nothing. A cluster is then exactly as it was before anybody installed this — every member
    /// on the address it already had.
    /// </remarks>
    public bool Configured =>
        !string.IsNullOrWhiteSpace(Bases.Zone)
        && !string.IsNullOrWhiteSpace(ProviderZoneId)
        && !string.IsNullOrWhiteSpace(ProviderToken);

    internal static AnchorOptions FromSettings(AnchorSettings settings)
    {
        string memberId = string.IsNullOrWhiteSpace(settings.MemberId)
            ? Environment.MachineName.ToLowerInvariant() + "-dns"
            : settings.MemberId.Trim();

        var bases = new ClusterBases(
            Zone: settings.Zone.Trim().Trim('.'),
            Nodes: Label(settings.NodesBase, "nodes"),
            Anchors: Label(settings.AnchorsBase, "anchors"),
            Play: Label(settings.PlayBase, "play"));

        return new AnchorOptions(
            MemberId: memberId,
            ListenAddress: string.IsNullOrWhiteSpace(settings.ListenAddress)
                ? "http://127.0.0.1:8099"
                : settings.ListenAddress.Trim(),
            PublicBaseUrl: settings.PublicBaseUrl.Trim().TrimEnd('/'),
            PublicAddress: settings.PublicAddress.Trim(),
            Bases: bases,
            MemberNames: new Names.MemberNamePool(Csv(settings.MemberNames), Pins(settings.MemberNamePins)),
            Provider: string.IsNullOrWhiteSpace(settings.Provider) ? "cloudflare" : settings.Provider.Trim(),
            ProviderZoneId: settings.ProviderZoneId.Trim(),
            ProviderToken: settings.ProviderToken.Trim(),
            Ttl: Math.Max(AnchorSettings.Floors.TtlSeconds, settings.TtlSeconds ?? 60),
            Reconcile: TimeSpan.FromSeconds(
                Math.Max(AnchorSettings.Floors.ReconcileSeconds, settings.ReconcileSeconds ?? 300)),
            RegistryPath: settings.RegistryPath.Trim(),
            ClusterStorePath: settings.ClusterStorePath.Trim(),
            ConfigDescriptorPath: settings.ConfigDescriptorPath.Trim());
    }

    private static string Label(string written, string fallback)
    {
        string label = DnsLabel.Sanitize(written);
        return DnsLabel.IsValid(label) ? label : fallback;
    }

    private static IReadOnlyList<string> Csv(string written) =>
        [.. written.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>
    /// The pins, as <c>member=label</c> pairs. An entry that is not a pair is skipped: a pin nobody can
    /// parse would otherwise read as a member id nothing matches, which looks identical to no pin at all
    /// right up until the wrong machine is renamed.
    /// </summary>
    private static IReadOnlyDictionary<string, string> Pins(string written)
    {
        var pins = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (string entry in Csv(written))
        {
            int split = entry.IndexOf('=', StringComparison.Ordinal);
            if (split <= 0 || split == entry.Length - 1)
                continue;

            pins[entry[..split].Trim()] = entry[(split + 1)..].Trim();
        }

        return pins;
    }
}
