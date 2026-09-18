using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using TheKrystalShip.KGSM.Cluster.Messaging;
using TheKrystalShip.KGSM.Dns.Serving;

namespace TheKrystalShip.KGSM.Dns.Member;

/// <summary>Where a component keeps its certificates and writes its generated site.</summary>
/// <remarks>
/// The defaults are the directories every KGSM component's setup provisions, one subdirectory or file
/// per component, so two components on one machine never write each other's files.
/// </remarks>
public static class DnsServing
{
    /// <summary>Where keys and certificates live, one directory per component.</summary>
    public const string TlsRoot = "/var/lib/kgsm/tls";

    /// <summary>Where generated sites are written; the web server includes every file in it.</summary>
    public const string SitesDirectory = "/var/lib/kgsm/nginx";

    /// <summary>Where each component's own proxy rules are installed.</summary>
    public const string LocationsDirectory = "/etc/nginx/kgsm";

    /// <summary>The serving options for one component at the default locations.</summary>
    public static NginxSiteOptions For(string component) => new(
        component, SitesDirectory, Path.Combine(LocationsDirectory, component + ".locations"));
}

/// <summary>Registers what a member needs to be named by the cluster's DNS anchor, and served under those names.</summary>
public static class DnsMemberServiceCollectionExtensions
{
    /// <summary>
    /// Tell the DNS anchor where this member is reached, keep what it answers, and keep a certificate and
    /// a served site for every name it gives this member: a node's own name, and the name of every
    /// capability the member holds.
    /// </summary>
    /// <remarks>
    /// Inert on a member that is not in a cluster, and harmless in a cluster nobody holds <c>dns</c> in:
    /// the statement goes to the holder, and with no holder there is nobody to send it to. On a host not
    /// provisioned to serve generated sites the names are kept and nothing is served. Requires the
    /// cluster package to be registered.
    /// </remarks>
    /// <param name="services">The member's service collection.</param>
    /// <param name="publicHost">Where this member is reached from the internet — normally its network's
    /// dynamic-DNS name.</param>
    /// <param name="component">The component serving this member's names, such as <c>kgsm-api</c>.</param>
    public static IServiceCollection AddKgsmDnsMember(
        this IServiceCollection services, string publicHost, string component)
    {
        services.AddSingleton(new DnsMemberOptions(publicHost.Trim(), TimeSpan.FromMinutes(15)));
        services.AddSingleton<DnsNameBook>();
        services.AddSingleton<IServedNames>(sp => sp.GetRequiredService<DnsNameBook>());

        services.AddSingleton(new CertificateFiles(Path.Combine(DnsServing.TlsRoot, component)));
        services.AddSingleton(sp => new NginxSite(
            DnsServing.For(component),
            sp.GetRequiredService<CertificateFiles>(),
            sp.GetRequiredService<ILogger<NginxSite>>()));
        services.AddSingleton<ICertificateRequester, BusCertificateRequester>();
        services.AddSingleton<CertificateKeeper>();
        services.AddHostedService(sp => sp.GetRequiredService<CertificateKeeper>());

        services.AddSingleton<IClusterMessageHandler, DnsNamesHandler>();
        services.AddSingleton<IClusterMessageHandler, DnsCertHandler>();
        services.AddHostedService<DnsSelfWorker>();
        return services;
    }
}
