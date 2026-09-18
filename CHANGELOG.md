# Changelog

All notable changes to `kgsm-dns` are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.8.0]

### Added — a member is advertised at the names it serves

A member is reached at the names the anchor gave it, so those are the addresses the cluster advertises
for it: every name its generated site serves with a certificate, node name first, ahead of every
address it was reflected at and behind only a configured public address. A member therefore needs no
configured address once it is in a cluster with a DNS anchor.

- **Served, not merely held.** A published name with no certificate installed yet answers no handshake
  here and is not offered.
- **A restart advertises what the site on disk already serves**, before the anchor has answered, since
  the web server has been serving that file all along. An expired certificate's name is not offered.
- **`TheKrystalShip.KGSM.Dns 0.2.0-dev.7`**: `DnsSelfAddresses`, registered by `AddKgsmDnsMember` as the
  cluster package's `ISelfAddressSource`, and `NginxSite.Served`. Requires `TheKrystalShip.KGSM.Cluster
  1.0.0-dev.20`.

## [0.7.0]

### Added — game servers that want one port behind one router are flagged

Members behind one router share one public address and one port space. The anchor keeps the ports each
game server declares, resolves every node's stated host on each zone pass, and flags every pair of
servers on different nodes that share an address and declare an overlapping port on the same protocol,
on both. The names stay published: which of the two runs is the router's and the hosts' to decide.

- **Carried to the owner on `dns.names`**: each game name's entry has `collisions` — the other server,
  its member and the ports both want — so the node can show them beside the server.
- **Unknown is not none.** A node with no stated host or an unresolvable one, a server whose ports the
  anchor has not been told, and every server that could share an address with either, carry null rather
  than an empty list. A node that stated it has no public host shares no address with anything.
- **Two servers on one node are not compared**; the engine answers for those.
- **`TheKrystalShip.KGSM.Dns 0.2.0-dev.6`**: `DnsCollision`, `DnsNameEntry.Collisions`, and
  `DnsNameBook.GameCollisions(instance)`. An entry from an anchor that says nothing about collisions
  reads as unknown.

## [0.6.0]

### Added — every game server has a name players connect through

A node tells the anchor every game server installed on it (`dns.instances`), and the anchor names each
`<game>.play.<zone>`, an alias of the node's own name, so a node's address change moves no game record.
The label is the blueprint reduced to a label; a second server of the same game anywhere in the
cluster takes the lowest free suffix (`factorio-2`), and a released label is free again.

- **The set is the whole truth about one node.** It is sent only from a successful read of the engine,
  so an instance missing from it has been uninstalled and loses its name; a failed read sends nothing
  and releases nothing. A node's list touches that node's names only.
- **A name is keyed on the node and the instance**, so restating keeps every name.
- **A game name follows its node's own name**: blocked while the node has none or cannot be published,
  and published once it can, re-derived on every tick.
- **`TheKrystalShip.KGSM.Dns 0.2.0-dev.5`**: the message shapes, `AddKgsmDnsGameNames<TSource>()` with
  `IDnsInstanceSource` for a node to read its engine through, and `DnsNameBook.GameName(instance)`.
  Level-triggered: the source is read every few seconds and the set sent when it changes, when the
  holder of `dns` changes, and on a slow refresh.

## [0.5.0]

### Added — every capability the cluster assigns has a name that follows its holder

`<capability>.anchors.<zone>` is kept for every held capability, pointing at the host its holder said
it is reached at, and re-derived from the cluster's assignments on every tick. A reassignment moves
the record within seconds and tells both the member that lost the name and the one that gained it,
and the holder then asks for a certificate for it the way a node asks for its own.

- **Every member states its host**, anchors included. The anchor keeps the latest statement per
  member; only a node is named from it.
- **A holder the anchor has not heard from** keeps a name it already held where it was, because not
  having heard is not having been told otherwise. A name moving to such a holder is withdrawn until it
  speaks, rather than left resolving to the member that no longer holds it.
- **A capability held by nobody loses its name.**
- **A blocked name's record is withdrawn.** A name that cannot be published — no host, or a host
  nobody outside could reach — no longer leaves the record it had before it was blocked.
- **`TheKrystalShip.KGSM.Dns 0.2.0-dev.4`**: the member half is any member's, and logs the set of
  names it is reached at when that set changes. A member the anchor tells it holds nothing withdraws
  the site it served, so the member that lost a capability stops serving its name; before the anchor's
  first answer after a start, an empty set is not yet told and withdraws nothing.

## [0.4.0]

### Added — the anchor issues every certificate in the cluster

A member keeps a key per name it holds and asks the anchor for a certificate against a signing
request; the anchor has it issued over ACME, answering the DNS-01 challenge through the zone it already
holds the credential for, and sends the chain back. The key never leaves the member. The anchor's own
name is served the same way, with no bus between its request and the issuer.

- **The ACME order flow is written against RFC 8555**, because the anchor is Native AOT: an account
  registered per holder with a key it generates, one order at a time off the request path, ES256
  signatures, and a retry on a stale nonce.
- **A challenge is confirmed at the zone's own nameservers** before the issuer is told to look, asked
  directly over UDP so no resolver's cached negative answer can stand in for what the zone holds.
- **A request is checked before it is ordered**: the sender must hold the name, published, and the
  request must name that one name and nothing else, signed by its own key.
- **Serving** is a generated site per component in `/var/lib/kgsm/nginx/`, one server block per name
  with a certificate, including the component's own proxy rules; the member reloads nginx through a
  grant that allows nothing else and confirms the served certificate over a TLS handshake. Renewal
  falls due in the last third of a certificate's lifetime and is asked for by the member.
- **`TheKrystalShip.KGSM.Dns 0.2.0-dev.2`** carries the member half: `AddKgsmDnsMember(publicHost,
  component)`.
- **The package ships the serving pieces**: the proxy rules, the include, the directories as
  `tmpfiles.d` entries, and the reload grant for the service account.

## [0.3.0]

### Added — nodes are named by the anchor they tell where they are reached

A node states its public host on the bus (`dns.self`) and the anchor answers with every name it holds
(`dns.names`). The anchor names a node from its list the first time it hears from it, publishes the
name as an alias of the stated host, and tells the node again after the zone pass that published it.
Only a node is named, read from the anchor's own roster rather than from the message, and a statement
only ever moves the sender's own names. The host is trusted as stated: checking it against the address
the node calls from would refuse correct configurations on dual-stack and multi-link networks.

- **`TheKrystalShip.KGSM.Dns 0.2.0-dev.1`** carries the two messages and the node's half,
  `AddKgsmDnsMember`: the statement, repeated on startup, on a change of holder or host, and on a slow
  refresh, and the book of names the anchor answered with — accepted only from the member holding
  `dns`.
- **Claims are serialized.** The inbox and the zone worker claim at once, and a draw that read and wrote
  unguarded could give two nodes one name.
- **The cluster's values ship in the settings file**: the zone, its id, the name list and the pins. The
  env file carries the provider token and where the anchor's own machine is reached.

## [0.2.0]

### Changed — names point at dynamic-DNS hosts, and only nodes hold node names

A name is a `CNAME` to its holder's public host, which on a home connection is the dynamic-DNS name the
network already keeps pointed at a changing address. The cluster's record then never moves when the
address does. A fixed public address is still accepted and becomes an address record; a private,
loopback, link-local or carrier-grade NAT address, or one of the cluster's own names, is refused with
the reason.

`Dns__PublicAddress` is `Dns__PublicHost`, and takes a hostname.

An anchor holds no node name: it is reached by the capability it holds, and a node name found under an
anchor's id is released. A machine running a node beside several anchors carries one node name.

A name pinned to a member is reserved for it before it joins, so a node joining first cannot draw the
name somebody else is promised.

## [0.1.1]

### Fixed — a holder that lost its store keeps its name

The registry is read against the zone before anything claims a name. A holder whose store was gone
drew itself a fresh name on its first pass, and the reconcile after it withdrew the name the zone
already carried for that member — measured on the live zone, where a restart with an empty store
renamed the anchor and deleted its record. The claim now waits for the read, and a holder that cannot
read the zone claims nothing until it can.

A refusal for a name another record holds is recognised by the provider's error code rather than its
wording, which is prose the provider is free to rephrase.

## [0.1.0]

### Added — the anchor publishes

The `dns` capability, the name registry, the Cloudflare provider and the reconcile that brings a zone
to what the registry says.

- **`TheKrystalShip.KGSM.Dns 0.1.0`** — the names themselves: labels reduced from suggestions that
  were never shaped for DNS, the three bases each kind of name lives under, and the mark a published
  record carries.
- **The registry** records every claimed name with what it points at and where it stands. It is
  rebuildable from the marks, so taking the capability over moves no state between members.
- **The reconcile** creates, corrects and withdraws records in one direction, leaves anything without
  this cluster's mark alone, and recovers rows from the zone on a first pass over an empty registry.
- **Member and capability names.** A member is drawn a name from the anchor's list, or keeps one it is
  pinned to, and holds it from then on; a capability's name follows whichever member the cluster
  assigns it to.
- **Standing.** The anchor claims `dns` only into an empty assignment, re-reads who holds it, and
  writes nothing at all while another member does.
