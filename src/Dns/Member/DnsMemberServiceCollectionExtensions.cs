using Microsoft.Extensions.DependencyInjection;

using TheKrystalShip.KGSM.Cluster.Messaging;

namespace TheKrystalShip.KGSM.Dns.Member;

/// <summary>Registers what a node needs to be named by the cluster's DNS anchor.</summary>
public static class DnsMemberServiceCollectionExtensions
{
    /// <summary>
    /// Tell the DNS anchor where this node is reached, and keep what it answers.
    /// </summary>
    /// <remarks>
    /// Inert on a member that is not in a cluster, and harmless in a cluster nobody holds <c>dns</c> in:
    /// the statement goes to the holder, and with no holder there is nobody to send it to. Requires the
    /// cluster package to be registered.
    /// </remarks>
    /// <param name="services">The member's service collection.</param>
    /// <param name="publicHost">Where this node is reached from the internet — normally its network's
    /// dynamic-DNS name.</param>
    public static IServiceCollection AddKgsmDnsMember(this IServiceCollection services, string publicHost)
    {
        services.AddSingleton(new DnsMemberOptions(publicHost.Trim(), TimeSpan.FromMinutes(15)));
        services.AddSingleton<DnsNameBook>();
        services.AddSingleton<IClusterMessageHandler, DnsNamesHandler>();
        services.AddHostedService<DnsSelfWorker>();
        return services;
    }
}
