using TheKrystalShip.KGSM.ComponentConfig;

// What this daemon can be configured with, declared beside the configuration it describes. The
// generator reads this out of the built assembly and writes deploy/kgsm-dns.anchor.json; deploy.sh
// installs that into /var/lib/kgsm/anchors/dns-anchor.json. The daemon itself never reads any of this.

// An anchor, not a leaf. It serves one capability to the whole cluster and is a peer of every node in
// it: the names it publishes are the cluster's, not one machine's, and the credential it holds is the
// reason no node carries one. So it is described in /var/lib/kgsm/anchors/ rather than where a node's
// leaves are scanned for, and the Control Panel reaches it off the cluster's Anchors card.
[assembly: Anchor(
    id: "dns-anchor",
    displayName: "DNS anchor",
    unit: "kgsm-dns.service",
    role: "Publishes the names a cluster is reached at — members, capabilities and game servers — and is the only member holding a credential for the zone.")]

// Panel sections, in the order they render. Fields land in one by naming its id, and follow the order
// they are declared in AnchorSettings.
[assembly: ConfigGroup("network", "Network", 1)]
[assembly: ConfigGroup("naming", "Naming", 2)]
[assembly: ConfigGroup("provider", "Provider", 3)]
[assembly: ConfigGroup("certificates", "Certificates", 4)]
[assembly: ConfigGroup("storage", "Storage", 5)]
[assembly: ConfigGroup("general", "General", 6)]

// Where this daemon's own configuration comes from, lowest precedence first — the same order Program.cs
// resolves them in. The settings file is the base the other two override one key of.
[assembly: ConfigFloorSource("appsettings", "/opt/kgsm-dns/kgsm-dns.settings.json")]
[assembly: ConfigFloorSource("systemd-unit", "kgsm-dns.service")]
[assembly: ConfigFloorSource("env-file", "/etc/kgsm-dns/kgsm-dns.env")]

// Per-category log filtering can name any category there is, so the namespace cannot be enumerated.
// Every other key in the settings file has to be described or the build fails.
[assembly: ConfigFrameworkNamespace("Logging__",
    "per-category filtering is open-ended: any category name is a valid key")]

// The cluster secret is one value every member on a machine reads from one shared file, so it is owned
// by the cluster package rather than by any member's own settings.
[assembly: ConfigFrameworkNamespace("Cluster__",
    "the cluster secret is host-level: every member on a machine reads the same file")]

// The ecosystem logging level. It has no AnchorSettings property because Microsoft.Extensions.Logging
// owns it, so it is the one key nothing in this daemon's own types can be read to discover.
[assembly: ConfigFrameworkField("logLevel", "Logging__LogLevel__Default", "Log level",
    Description = "Minimum severity this anchor logs.",
    Group = "general",
    Type = ConfigType.Enum,
    Values = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"])]
