namespace TheKrystalShip.KGSM.Dns;

/// <summary>
/// The cluster capability this component holds.
/// </summary>
/// <remarks>
/// One holder per cluster, because the holder is the only member carrying a credential for the zone
/// and the only writer of the name registry. A second install is a candidate: it claims only into an
/// empty assignment and stands by otherwise, which is what
/// <c>TheKrystalShip.KGSM.Cluster.Membership.ClusterCapabilityWorker</c> enforces for every capability.
/// </remarks>
public static class DnsCapability
{
    /// <summary>The capability name, as the cluster's assignment records it.</summary>
    public const string Name = "dns";
}
