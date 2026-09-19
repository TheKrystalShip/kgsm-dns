# kgsm-dns

The names a KGSM cluster is reached at, and the one member that publishes them.

Every node, every capability and every game server has its own name under one domain, and a machine
is configured with none of them: a member joins knowing only where to ask, states where it is reached,
and is given a name. One anchor holds the zone's credential and writes every record; every other member
asks it.

```
<node>.nodes.example.com          a node — a browser and the other members reach it here
<capability>.anchors.example.com  a capability — follows whichever member holds it
<game>.play.example.com           a game server — players connect here, directly
```

Authority for the design is `cluster-dns-plan.md` in the workspace.

## Why the names differ per server

A game client resolves a name and then opens a socket to an address. The only lookup it makes is
DNS, which carries a name and never a port, so telling servers apart by port alone would need
something in the middle relaying every packet. That is ruled out: it adds latency and hides the
player's address, and moderation depends on seeing it. So each server has its own name, and the
pattern is one a player can guess.

## What it publishes

| kind | record | points at |
|---|---|---|
| node | `CNAME` | the node's public host — normally its network's dynamic-DNS name |
| capability | `CNAME` | the public host of the member currently holding the capability |
| game server | `CNAME` | its node's name |

A home connection's address moves whenever its provider likes, and the network's dynamic-DNS client
already follows it. Pointing at that name means a cluster record never has to move when the address
does. A member with a fixed address states the address instead, and gets an `A` or `AAAA`.

A name only ever points at where its own holder is reached, so a member can never have a name pointing
at somebody else's machine. The names a member serves are also the addresses the cluster advertises for
it, so a member in a cluster with a DNS anchor is configured with no address of its own. Only nodes are named under `nodes.`: a machine running a node and several
anchors carries one node name, and each anchor is reached by the capability it holds.

Every member, node or anchor, tells the anchor where it is reached, and the anchor keeps a name for
every capability the cluster assigns, pointing at the host its holder stated. The names are derived
again from the cluster's assignments every few seconds, so reassigning a capability moves its name, and
the new holder is told it holds it and asks for its certificate. A capability held by nobody has no
name.

A node tells the anchor every game server its engine reports, as a complete set and only from a read
that succeeded. Each is named after its game, with the lowest free suffix when another server of the
same game already holds the name anywhere in the cluster (`factorio`, `factorio-2`), and a server
missing from the set has been uninstalled and loses its name. A game name is an alias of its node's
name, so it follows the node wherever the node's own name points.

Nodes behind one router share one public address and one port space. On every zone pass the anchor
resolves each node's host and flags every pair of servers on different nodes that share an address and
declare an overlapping port on the same protocol, on both, telling each node about its own. The names
stay published. A server that could not be checked is reported as unknown, never as clear.

Every record carries a mark saying which cluster name it is and who holds it. A record without one
is never edited, deleted, or counted as satisfying a name: a zone holds records people made by hand,
and the name alone is not evidence of who made it. The marks are also what the registry is rebuilt
from, so a member taking the capability over converges on what the previous holder published with
nothing having moved between them.

## Certificates

Every name a member serves over HTTPS has a certificate the anchor issued. The member generates the key
and sends only a signing request; the anchor orders the certificate over ACME, proves the name through
a DNS-01 challenge it writes into the zone, and sends the chain back. A member therefore needs no
certbot, no open port 80, and nothing reachable before its first certificate exists.

The member serves each name from a generated site, `/var/lib/kgsm/nginx/<component>.conf`, holding
only names and certificate paths and including the component's own reviewed proxy rules. It reloads
nginx through a grant that allows nothing else and checks the result over a real TLS handshake.
Certificates are renewed in the last third of their lifetime.

## What a person can read

The anchor answers a browser directly, so a cluster's names have a page whether or not any node runs
a Control Panel API. Nothing a browser sends here reaches the zone directly — every write below goes
through the same entitlement checks the zone pass itself runs under.

| route | answers |
|---|---|
| `GET /api/v1/cluster/dns` | the whole picture as one `DnsStatus`: this member's standing (holder or standing by, configured, zone, bases, cadence, issuer), the zone's health (last pass, its duration, error and what it changed, next pass, contested names), every name with its target, state, block reason, declared ports, cross-member port collisions and — for an alias — the game name it follows, every member with where it said it is reached, when, and what that resolved to, and every certificate order with its expiry, when it will next be renewed, and the counts issued and failed in the last week |
| `GET /api/v1/cluster/dns/events?limit=` | the newest lines of the anchor's own event journal, newest first, as the journal holds them |

A caller presents the session the cluster's auth anchor minted. It verifies here against the key that
member publishes through gossip, and the person's authority is read on every request from the anchor's
own copy of the cluster's accounts — a viewer or above is admitted to the reads above, a disabled account
is signed out, and a session ended elsewhere is refused from the moment the announcement arrives. An
anchor that has not yet heard the key, or has no copy of the accounts yet, answers `503` naming the
reason rather than `401`. The Control Panel's origin is listed in `AllowedOrigins`.

Unknown is never reported as none. A pass that has not run, a host that did not resolve, a collision
that could not be checked and a certificate issued before anything recorded it are all null.

## What an admin can write

Every route below needs admin, the same accounts copy checked on every read, and refuses with a body
naming why on a member standing by for the capability or one with no zone and credential configured —
such a member writes nothing, the same as the zone pass itself.

| route | does |
|---|---|
| `POST /api/v1/cluster/dns/zone/check` | brings the next zone pass forward to now |
| `POST /api/v1/cluster/dns/certificates/{name}/renew` | asks for that name's certificate to be renewed ahead of its own schedule — `404` for a name this anchor does not hold, `409` for one that takes no certificate (a game name or an alias) or is not yet published |
| `POST /api/v1/cluster/dns/aliases` (`{ "server", "label" }`) | names an existing, published game server under an additional label in the play base — the same entitlement its game name has, following the same node and released with it — `409` when the name is already held |
| `DELETE /api/v1/cluster/dns/aliases/{name}` | gives up an alias; its server keeps its own name and every other alias |

**The journal** is `/var/lib/kgsm-dns/events/`, in the ecosystem's envelope, and records transitions:
`dns.name.published`, `.moved`, `.withdrawn`, `.contested`, `.recovered`; `dns.certificate.issued`,
`.failed`, `.refused`, `.renew_requested`; `dns.zone.unreachable`, `.restored` and `.check_requested`;
`dns.alias.added` and `.removed`. A quiet pass writes nothing. Names and payloads are the package's
(`DnsEvents`, `DnsEventPayloads`); an admin action's actor is the person, `provider:username`.

## Packages

| package | contents | taken by |
|---|---|---|
| **`TheKrystalShip.KGSM.Dns`** | labels, the three bases, the mark a published record carries, the bus messages, the status document and journal events a reader consumes, and a member's half: stating where it is reached, keeping its names, and keeping a certificate and a served site for each. AOT-safe. | every cluster member |

The deployable is **`kgsm-dns`** (`src/Dns.Anchor`), shipped as a pacman package and a systemd unit.

## Running it

```bash
./deploy/setup.sh     # ONCE per host. Asks for sudo.
./deploy/deploy.sh    # every deploy. No sudo, no prompts.
```

It needs a zone, the provider's id for it, and an API token, in `/etc/kgsm-dns/kgsm-dns.env`. Without
them it holds the capability, reports itself unconfigured, and publishes nothing — a cluster then
looks exactly as it did before anybody installed this.

```bash
dotnet test kgsm-dns.slnx -c Release
dotnet publish src/Dns.Anchor/Anchor.csproj -c Release -r linux-x64   # expect 0 ILC warnings
```
