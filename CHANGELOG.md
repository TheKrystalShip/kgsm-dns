# Changelog

All notable changes to `kgsm-dns` are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
