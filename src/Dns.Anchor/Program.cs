using Microsoft.Extensions.Caching.Memory;

using TheKrystalShip.KGSM.Auth.Cluster;
using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Cluster.Messaging;
using TheKrystalShip.KGSM.Dns;
using TheKrystalShip.KGSM.Dns.Anchor;
using TheKrystalShip.KGSM.Dns.Anchor.Access;
using TheKrystalShip.KGSM.Dns.Anchor.Names;
using TheKrystalShip.KGSM.Dns.Anchor.Providers;
using TheKrystalShip.KGSM.Dns.Anchor.Acme;
using TheKrystalShip.KGSM.Dns.Anchor.Issuance;
using TheKrystalShip.KGSM.Dns.Anchor.Registry;
using TheKrystalShip.KGSM.Dns.Anchor.Status;
using TheKrystalShip.KGSM.Dns.Member;
using TheKrystalShip.KGSM.Dns.Serving;
using TheKrystalShip.KGSM.Extensions;

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
builder.Services.AddSingleton<Departures>();
builder.Services.AddSingleton<IHostResolver, SystemHostResolver>();
builder.Services.AddSingleton<PortCollisions>();

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

// What this anchor did, in its own event journal under its state directory, where every reader of the
// ecosystem's journals finds it by the producer id alone. And what the last zone pass did, held in memory
// for the status page, since the next pass says it again.
builder.Services.AddKgsmJournal(DnsJournal.ProducerId, typeof(DnsJournal).Assembly);
builder.Services.AddSingleton<DnsJournal>();
builder.Services.AddSingleton<ZonePassLog>();

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
// Finished orders go in the registry's file, which the registry has already created by the time this
// opens it.
builder.Services.AddSingleton(sp =>
{
    sp.GetRequiredService<SqliteNameRegistry>();
    return new CertificateOrders(options.RegistryPath);
});
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

// A person's browser using this anchor's page. It signs in once, at the member holding `auth`; the
// session verifies here against the key that member publishes, and what the person may do is evaluated
// from this anchor's own replica of the authority that member publishes. Every piece is the shared one
// every member verifies and evaluates with, and inert outside a cluster: no holder is heard of, no key
// is learned, and every session is refused as unverifiable.
builder.Services.AddMemoryCache();

// The replica is this member's alone: it lives in this anchor's state directory, shares no file with any
// other member on the machine, and is filled by the snapshot on joining and every change after.
builder.Services.AddSingleton<IReplicatedAuthority>(sp => new OwnedReplicaFile(
    sp.GetRequiredService<AnchorOptions>().AccountsPath, sp.GetRequiredService<ILogger<OwnedReplicaFile>>()));
builder.Services.AddAuthorityReplica();
builder.Services.AddSingleton<MemberAccess>();

// What this anchor performs, told to the holder of the accounts: its own manifest, where the deploy
// installs it, which is what makes its dns:* actions something a role can hold.
builder.Services.AddSingleton(new AuthorityReporterOptions
{
    ManifestFiles = ["/var/lib/kgsm/anchors/actions/dns-anchor.json"],
});
builder.Services.AddSingleton<AuthorityReporter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AuthorityReporter>());

builder.Services.AddSingleton<ClusterSessionKeys>();
builder.Services.AddSingleton<IClusterSessionKeys>(sp => sp.GetRequiredService<ClusterSessionKeys>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ClusterSessionKeys>());

// A sign-out elsewhere, announced over the bus, refused here from then on. The record is kept for as long
// as a session somebody could still present is bounded by, which is the longest a person stays signed in.
builder.Services.AddSingleton<EndedSessions>();
builder.Services.AddSingleton<IClusterSessionDenyList>(sp => sp.GetRequiredService<EndedSessions>());
builder.Services.AddSingleton(sp => new ClusterSessionRevocations(
    sp.GetRequiredService<IClusterSessionDenyList>(),
    sp.GetRequiredService<IMemoryCache>(),
    TimeSpan.FromSeconds(5)));
builder.Services.AddSingleton<IClusterMessageHandler>(sp => new SessionRevokeHandler(
    sp.GetRequiredService<IClusterSessionDenyList>(),
    sp.GetRequiredService<ClusterSessionRevocations>(),
    TimeSpan.FromDays(30),
    sp.GetRequiredService<ILogger<SessionRevokeHandler>>()));

builder.Services.AddSingleton<ClusterBearer>();

// The Control Panel is served from another origin, with a bearer and never a cookie. The writes below
// need POST and DELETE and a JSON body.
builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
    .WithOrigins([.. options.AllowedOrigins])
    .WithMethods("GET", "POST", "DELETE")
    .WithHeaders("Authorization", "Content-Type")));

WebApplication app = builder.Build();

app.UseCors();

// The member wire: the inbox other members deliver to, and the handshake a join is performed through.
// Served by the package rather than spelled out here, so every member answers the same routes.
app.MapClusterEndpoints();

// A member fetching its own names, for when it cannot yet be reached to be told them.
app.MapGet(NamesEndpoint.Path, NamesEndpoint.HandleAsync);

// What a person's browser reads: the cluster's names as this anchor holds them, and its journal.
StatusEndpoints.Map(app);

// What the caller may do with this anchor, so the panel gates its controls by lookup.
MeAccessEndpoint.Map(app);

// What a browser writes: a zone check, a certificate renewal, and a game server's aliases. Each its own
// dns:* action, and only on the holder — see AdminEndpoints's own remarks.
AdminEndpoints.Map(app);

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
