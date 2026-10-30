using TheKrystalShip.KGSM.ComponentConfig;

namespace TheKrystalShip.KGSM.Dns.Anchor;

/// <summary>
/// The anchor's configurable surface, shaped 1:1 with the <c>"Dns"</c> section of
/// <c>kgsm-dns.settings.json</c>. That file is the floor: every knob is declared there with its
/// default, and an environment variable may only override a key that exists in it
/// (<c>Dns__Zone</c>, <c>Dns__ProviderToken</c>). A variable naming a key this class does not declare
/// sets nothing.
/// </summary>
/// <remarks>
/// This type holds what was <em>written</em>; <see cref="AnchorOptions"/> is the validated form, so the
/// raw configuration and the runtime view stay separable. Every number is nullable, and null means
/// "not written" — a blank value in an env file binds to a non-nullable int by throwing, and a JSON
/// null binds to zero, so nullable is what turns both into "use the coded default".
/// </remarks>
[ConfigSection(Section)]
internal sealed class AnchorSettings
{
    /// <summary>The configuration section this binds from.</summary>
    public const string Section = "Dns";

    /// <summary>The lowest value each interval may take.</summary>
    public static class Floors
    {
        /// <summary>A TTL under a minute buys nothing and costs a query per client per minute.</summary>
        public const int TtlSeconds = 30;

        /// <summary>A reconcile faster than this is a listing of the whole zone in a loop.</summary>
        public const int ReconcileSeconds = 30;
    }

    /// <summary>Where Kestrel listens. Loopback-fronted, like every other member's surface.</summary>
    /// <panel>The address this anchor listens on. It sits behind the machine's web server, which holds
    /// the certificate and answers the public name.</panel>
    [ConfigField("listenAddress", "Listen address", Group = "network", Risk = ConfigRisk.Wiring)]
    public string ListenAddress { get; set; } = "http://127.0.0.1:8099";

    /// <summary>This anchor's identity as a cluster member. Blank derives one from the machine name.</summary>
    /// <panel>The name other members of the cluster know this anchor by. A machine can run more than
    /// one member — a node and an anchor are two members with two names — so this is not the machine's
    /// name. Blank derives one from it.</panel>
    [ConfigField("memberId", "Cluster member id", Group = "network", Risk = ConfigRisk.Wiring,
        NoDefault = true)]
    public string MemberId { get; set; } = "";

    /// <summary>The address other members reach this anchor at, when it has one a person can state.</summary>
    /// <panel>The address other members of the cluster reach this anchor at. Set it when this machine
    /// sits behind a reverse proxy and cannot see its own public address; otherwise leave it blank and
    /// the addresses are learned when a member joins.</panel>
    [ConfigField("publicBaseUrl", "Public address", Group = "network", Risk = ConfigRisk.Wiring)]
    public string PublicBaseUrl { get; set; } = "";

    /// <summary>
    /// Where this machine is reached from the public internet: normally its dynamic-DNS hostname.
    /// </summary>
    /// <panel>Where this machine is reached from the internet — normally the dynamic-DNS name its
    /// network already keeps pointed at a changing home address, such as example.ddns.net. The
    /// capability's name points here, so an address change needs nothing from this anchor. A fixed
    /// public address works too. Blank leaves the capability's name unpublished.</panel>
    [ConfigField("publicHost", "This machine's public host", Group = "network", Risk = ConfigRisk.Wiring)]
    public string PublicHost { get; set; } = "";

    /// <summary>The DNS zone every name this cluster publishes lives in.</summary>
    /// <panel>The domain this cluster publishes names in, such as example.com. One domain is all a
    /// cluster needs: members, capabilities and game servers are named underneath it.</panel>
    [ConfigField("zone", "Zone", Group = "naming", Risk = ConfigRisk.Destructive)]
    public string Zone { get; set; } = "";

    /// <summary>The label members are named under.</summary>
    /// <panel>The part of the domain members are named under. With nodes, a member is reached at
    /// something like jessie.nodes.example.com.</panel>
    [ConfigField("nodesBase", "Members are named under", Group = "naming", Risk = ConfigRisk.Destructive)]
    public string NodesBase { get; set; } = "nodes";

    /// <summary>The label capabilities are named under.</summary>
    /// <panel>The part of the domain capabilities are named under. With anchors, the cluster's DNS anchor
    /// is reached at something like dns.anchors.example.com.</panel>
    [ConfigField("anchorsBase", "Capabilities are named under", Group = "naming",
        Risk = ConfigRisk.Destructive)]
    public string AnchorsBase { get; set; } = "anchors";

    /// <summary>The label game servers are named under.</summary>
    /// <panel>The part of the domain game servers are named under. With play, a server is played at
    /// something like minecraft.play.example.com.</panel>
    [ConfigField("playBase", "Game servers are named under", Group = "naming",
        Risk = ConfigRisk.Destructive)]
    public string PlayBase { get; set; } = "play";

    /// <summary>The names members are drawn from.</summary>
    /// <panel>The names members are given when they join, one of which is drawn at random for each.
    /// Nothing on a machine has to be configured with what that machine is called.</panel>
    [ConfigField("memberNames", "Member names", Group = "naming", Type = ConfigType.Csv,
        Risk = ConfigRisk.Safe)]
    public string MemberNames { get; set; } = "";

    /// <summary>Members that keep a stated name instead of drawing one, as <c>member=label</c>.</summary>
    /// <panel>Members that keep a name they already answer to, written as member=name. A machine people
    /// already reach at a name goes on being reached there when it joins.</panel>
    [ConfigField("memberNamePins", "Pinned member names", Group = "naming", Type = ConfigType.Csv,
        Risk = ConfigRisk.Wiring)]
    public string MemberNamePins { get; set; } = "";

    /// <summary>Which provider implementation acts on the zone.</summary>
    /// <panel>The DNS provider this cluster's zone is hosted with.</panel>
    [ConfigField("provider", "Provider", Group = "provider", Type = ConfigType.Enum,
        Values = ["cloudflare"], Risk = ConfigRisk.Wiring)]
    public string Provider { get; set; } = "cloudflare";

    /// <summary>The provider's own id for the zone.</summary>
    /// <panel>The provider's identifier for the zone. Its dashboard shows it beside the domain.</panel>
    [ConfigField("providerZoneId", "Zone id", Group = "provider", Risk = ConfigRisk.Wiring)]
    public string ProviderZoneId { get; set; } = "";

    /// <summary>
    /// The provider credential. Declared blank here; the value lives in the root-owned env file.
    /// </summary>
    /// <panel>The API token this anchor changes the zone with. It is held by this member alone, which
    /// is why every other member asks this one for a name instead of carrying a credential of its
    /// own.</panel>
    [ConfigField("providerToken", "API token", Group = "provider", Type = ConfigType.Secret,
        Risk = ConfigRisk.Destructive)]
    public string ProviderToken { get; set; } = "";

    /// <summary>How long resolvers may cache a name.</summary>
    /// <panel>How long other computers remember an answer before asking again. Short, because a member
    /// changing address or a capability moving to another member takes effect only once the old answer
    /// has expired everywhere.</panel>
    [ConfigField("ttlSeconds", "Record lifetime", Group = "provider", Risk = ConfigRisk.Safe,
        Min = Floors.TtlSeconds, Unit = "seconds")]
    public int? TtlSeconds { get; set; }

    /// <summary>How often the zone is compared with the registry.</summary>
    /// <panel>How often this anchor checks that the zone still says what the cluster asked it to say.
    /// It is what heals a record edited by hand and a write that failed halfway.</panel>
    [ConfigField("reconcileSeconds", "Zone check interval", Group = "provider", Risk = ConfigRisk.Safe,
        Min = Floors.ReconcileSeconds, Unit = "seconds")]
    public int? ReconcileSeconds { get; set; }

    /// <summary>The ACME directory certificates are issued from.</summary>
    /// <panel>The certificate issuer this anchor orders from. Let's Encrypt's production directory issues
    /// certificates browsers trust; its staging directory issues ones they do not, for trying the whole
    /// path without spending the issuer's limits.</panel>
    [ConfigField("acmeDirectory", "Certificate issuer", Group = "certificates", Risk = ConfigRisk.Wiring)]
    public string AcmeDirectory { get; set; } = "https://acme-v02.api.letsencrypt.org/directory";

    /// <summary>The address the issuer writes to about this anchor's account.</summary>
    /// <panel>An email address the issuer may write to about this cluster's certificates. Blank registers
    /// the account with no contact.</panel>
    [ConfigField("acmeContact", "Issuer contact", Group = "certificates", Risk = ConfigRisk.Safe)]
    public string AcmeContact { get; set; } = "";

    /// <summary>Where this holder's ACME account key is kept.</summary>
    /// <panel>The key this anchor's account with the issuer is registered under. Generated on first use
    /// and never copied: a member that takes this capability over registers its own.</panel>
    [ConfigField("acmeAccountKeyPath", "Issuer account key", Group = "certificates", Type = ConfigType.Path,
        Risk = ConfigRisk.Destructive)]
    public string AcmeAccountKeyPath { get; set; } = "/var/lib/kgsm-dns/acme-account.pem";

    /// <summary>Where the names this cluster has claimed are recorded.</summary>
    /// <panel>The file this anchor records every claimed name in. It can be rebuilt from the zone
    /// itself, because each record says which cluster name it is.</panel>
    [ConfigField("registryPath", "Name registry", Group = "storage", Type = ConfigType.Path,
        Risk = ConfigRisk.Destructive)]
    public string RegistryPath { get; set; } = "/var/lib/kgsm-dns/names.db";

    /// <summary>Where this member keeps its roster, outbox and inbox.</summary>
    /// <panel>The file this member keeps its cluster membership in — who else is in the cluster, and
    /// the messages owed to them.</panel>
    [ConfigField("clusterStorePath", "Cluster store", Group = "storage", Type = ConfigType.Path,
        Risk = ConfigRisk.Destructive)]
    public string ClusterStorePath { get; set; } = "/var/lib/kgsm-dns/cluster.db";

    /// <summary>Browser origins allowed to read this anchor's status, comma-separated.</summary>
    /// <panel>The browser origins allowed to read this anchor's page, comma-separated — normally the
    /// Control Panel's address. The panel is served from a different origin, so without an entry for it
    /// the browser refuses the answer before the page can read it.</panel>
    [ConfigField("allowedOrigins", "Allowed browser origins", Group = "access", Type = ConfigType.Csv,
        Risk = ConfigRisk.Wiring)]
    public string AllowedOrigins { get; set; } = "";

    /// <summary>Where the descriptor this build wrote is installed.</summary>
    /// <panel>The file describing what this anchor can be configured with, written by its own build and
    /// installed by its deploy. It is read to render this page; absent, there is no page.</panel>
    [ConfigField("configDescriptorPath", "Config descriptor", Group = "storage", Type = ConfigType.Path,
        Risk = ConfigRisk.Wiring)]
    public string ConfigDescriptorPath { get; set; } = "/var/lib/kgsm/anchors/dns-anchor.json";
}

/// <summary>
/// The sign-in provider whose tokens this anchor admits, bound from the "Sessions" section. Both values
/// are host-level, set once for every component on the machine in <c>/etc/kgsm/kgsm-cluster.env</c>
/// (<c>Sessions__Issuer</c>, <c>Sessions__Audience</c>).
/// </summary>
[ConfigSection(Section)]
internal sealed class SessionsSettings
{
    /// <summary>The configuration section this binds from.</summary>
    public const string Section = "Sessions";

    /// <summary>The provider's issuer, exactly as its tokens carry it.</summary>
    /// <panel>The sign-in provider this anchor admits people from, as its tokens name it. It is the same
    /// for every component on this machine, set in the shared cluster environment file. Blank, nobody
    /// is admitted to this anchor's page.</panel>
    [ConfigField("sessionsIssuer", "Sign-in provider", Group = "access", Risk = ConfigRisk.Wiring)]
    public string Issuer { get; set; } = "";

    /// <summary>The audience the cluster's tokens are minted for.</summary>
    /// <panel>The audience the cluster's access tokens are minted for; a token for any other is refused.
    /// It is the same for every component on this machine, set in the shared cluster environment
    /// file.</panel>
    [ConfigField("sessionsAudience", "Token audience", Group = "access", Risk = ConfigRisk.Wiring)]
    public string Audience { get; set; } = "";
}
