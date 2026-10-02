using TheKrystalShip.KGSM.ComponentConfig;

namespace TheKrystalShip.KGSM.Dns.Anchor.Access;

/// <summary>
/// The actions this anchor performs, each evaluated across the whole cluster: the names it publishes
/// are the cluster's, so there is no narrower target a grant on them could mean.
/// </summary>
/// <remarks>
/// <c>dns:journal.read</c> is the standard surface's, which the action manifest carries for every
/// component without a declaration here.
/// </remarks>
[Action(NamesRead, "See the cluster's names and certificates", DeclaredEffect.Read, DeclaredScope.Cluster)]
[Action(ZoneCheck, "Check the cluster's DNS zone now", DeclaredEffect.Execute, DeclaredScope.Cluster)]
[Action(CertificatesRenew, "Renew a member's certificate", DeclaredEffect.Execute, DeclaredScope.Cluster)]
[Action(AliasesWrite, "Add and remove names for game servers and capabilities", DeclaredEffect.Write, DeclaredScope.Cluster)]
internal static class DnsActions
{
    /// <summary>The namespace this anchor's actions are declared in.</summary>
    public const string Namespace = "dns";

    public const string NamesRead = "dns:names.read";

    public const string JournalRead = "dns:journal.read";

    public const string ZoneCheck = "dns:zone.check";

    public const string CertificatesRenew = "dns:certificates.renew";

    public const string AliasesWrite = "dns:aliases.write";
}
