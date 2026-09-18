using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Cluster.Messaging;
using TheKrystalShip.KGSM.Dns;
using TheKrystalShip.KGSM.Dns.Anchor;
using TheKrystalShip.KGSM.Dns.Anchor.Names;
using TheKrystalShip.KGSM.Dns.Anchor.Providers;
using TheKrystalShip.KGSM.Dns.Anchor.Acme;
using TheKrystalShip.KGSM.Dns.Anchor.Issuance;
using TheKrystalShip.KGSM.Dns.Anchor.Registry;
using TheKrystalShip.KGSM.Dns.Member;
using TheKrystalShip.KGSM.Dns.Serving;

var builder = WebApplication.CreateSlimBuilder(args);

// The daemon's settings file, loaded from beside the binary. Two reasons it is explicit:
//   1. CreateSlimBuilder under a systemd unit with no WorkingDirectory leaves the content root at "/",
//      so the framework's own appsettings.json discovery finds nothing and the file silently never
//      applies. AppContext.BaseDirectory is the binary's own directory, where deploy installs it.
//   2. It is named kgsm-dns.settings.json rather than appsettings.json, so it cannot collide with a
//      sibling ecosystem service's config if they ever share a directory.
builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "kgsm-dns.settings.json"),
    optional: true, reloadOnChange: false);

// Environment variables are re-registered so they sit last and therefore win. Configuration resolves
// by source order, and the file above was appended after the sources the builder installed — including
// its own environment provider. Without this line the file would outrank every Dns__ and Logging__
// variable, and an override would read as applied while changing nothing.
builder.Configuration.AddEnvironmentVariables();

// One journald-native sink with the <N> syslog priority prefix, so `journalctl -p` filters by level.
builder.Logging.ClearProviders();
builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
builder.Logging.AddSystemdConsole();

AnchorSettings settings =
    builder.Configuration.GetSection(AnchorSettings.Section).Get<AnchorSettings>() ?? new AnchorSettings();
AnchorOptions options = AnchorOptions.FromSettings(settings);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(options.Bases);
builder.Services.AddSingleton(options.MemberNames);

builder.WebHost.UseUrls(options.ListenAddress);

// Cluster membership. The secret is read through ClusterConfiguration so every member on the machine
// spells the key identically — one that spelled it differently would read a blank from a file that is
// not empty and quietly report itself standalone. The store is this member's own, inside its own
// StateDirectory=: two members sharing one would share a roster and an outbox.
var clusterOptions = new ClusterOptions
{
    MemberId = options.MemberId,
    Kind = MemberKind.Anchor,
    Secret = ClusterConfiguration.Secret(builder.Configuration),
    SecretPrevious = ClusterConfiguration.SecretPrevious(builder.Configuration),
    StorePath = options.ClusterStorePath,
    PublicBaseUrl = options.PublicBaseUrl,
};
builder.Services.AddKgsmCluster(clusterOptions);

builder.Services.AddSingleton(_ => new SqliteNameRegistry(options.RegistryPath));
builder.Services.AddSingleton<NameService>();
builder.Services.AddSingleton<CapabilityNames>();

// The zone, through whichever provider holds it. A timeout on the client because a provider that hangs
// must not hold a reconcile pass open: the pass is on a timer and the next one does the same work.
builder.Services.AddHttpClient(
    CloudflareProvider.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddSingleton<IDnsProvider>(sp => new CloudflareProvider(
    sp.GetRequiredService<IHttpClientFactory>(),
    new CloudflareOptions(options.ProviderZoneId, options.ProviderToken),
    sp.GetRequiredService<ILogger<CloudflareProvider>>()));

builder.Services.AddSingleton(sp => new RecordReconciler(
    sp.GetRequiredService<SqliteNameRegistry>(),
    sp.GetRequiredService<IDnsProvider>(),
    options.Bases,
    options.Ttl,
    sp.GetRequiredService<ILogger<RecordReconciler>>()));

builder.Services.AddSingleton<NamesAnnouncer>();

// The worker is a singleton as well as a hosted service, so the handler below can bring its next zone
// pass forward when a node says where it is reached.
builder.Services.AddSingleton<ClusterMembershipWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ClusterMembershipWorker>());

// A node saying where it is reached. Registered as a handler on the cluster's inbox, which has already
// checked that the sender is the member whose token signed the call.
builder.Services.AddSingleton<IClusterMessageHandler, SelfHandler>();
builder.Services.AddSingleton<IClusterMessageHandler, InstancesHandler>();

// Certificates. One issuer account per holder, whose key this member generates and never sends
// anywhere; challenges answered through the zone this member already holds the credential for; orders
// worked one at a time off the request path, because each takes the issuer a minute or two.
builder.Services.AddHttpClient(nameof(AcmeClient), c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton(sp => new AcmeClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(AcmeClient)),
    options.AcmeDirectory,
    options.AcmeContact,
    options.AcmeAccountKeyPath,
    sp.GetRequiredService<ILogger<AcmeClient>>()));
builder.Services.AddSingleton<IDns01Solver, ZoneDns01Solver>();
builder.Services.AddSingleton<IssuanceQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IssuanceQueue>());
builder.Services.AddSingleton<IClusterMessageHandler, CsrHandler>();

// This anchor's own names, served the same way every member serves its own: a key per name, a
// certificate kept current, and a generated site the web server includes. Only the request path
// differs — straight to the issuer, with no bus between.
builder.Services.AddSingleton<IServedNames, OwnNames>();
builder.Services.AddSingleton(new CertificateFiles(Path.Combine(DnsServing.TlsRoot, "kgsm-dns")));
builder.Services.AddSingleton(sp => new NginxSite(
    DnsServing.For("kgsm-dns"),
    sp.GetRequiredService<CertificateFiles>(),
    sp.GetRequiredService<ILogger<NginxSite>>()));
builder.Services.AddSingleton<ICertificateRequester, OwnCertificateRequester>();
builder.Services.AddSingleton<CertificateKeeper>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CertificateKeeper>());

WebApplication app = builder.Build();

// The member wire: the inbox other members deliver to, and the handshake a join is performed through.
// Served by the package rather than spelled out here, so every member answers the same routes.
app.MapClusterEndpoints();

// What this anchor is. Serialized through the source-generated context, because a Native AOT build has
// no reflection to fall back on.
app.MapGet("/health", (AnchorOptions o, ClusterOptions c) => Results.Json(
    new AnchorIdentity(
        Name: "kgsm-dns",
        Member: o.MemberId,
        Capability: DnsCapability.Name,
        Clustered: c.Enabled,
        Configured: o.Configured,
        Zone: o.Bases.Zone),
    AnchorJsonContext.Default.AnchorIdentity));

app.Run();
return 0;
