# Changelog

All notable changes to `kgsm-dns` are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.13.2]

### Fixed — a fact this anchor changes across a restart reaches the cluster

Takes `TheKrystalShip.KGSM.Cluster 1.0.0-dev.23`: a restart that re-publishes as many facts as before, one
of them different, no longer lands level with the incarnation other members hold and goes unheard.

## [0.13.1]

### Changed — the anchor's cluster state belongs to one cluster

Takes `TheKrystalShip.KGSM.Cluster 1.0.0-dev.22`: started with a different cluster secret, the anchor
discards the roster, the capability assignments and the queued messages it learned in the old cluster
before anything reads them, so a machine moving to another cluster never gossips its old `dns`
assignment into the new one.

## [0.13.0]

### Changed — a name and a certificate say what happened, and how much it matters

Every line this anchor records carries the three fields a reader renders from: the weight
(`info｜warn｜danger`), how it went, and one sentence of prose written as the fact is recorded. A name
freezes into that sentence as it was spelled then, so a later move leaves the earlier line still saying
where the name went at the time.

The weights follow what a change does to whoever holds the name. Publishing, correcting and recovering
a name leave it reachable and are the routine good news a pass exists for; a name leaving the zone or
held away from it stops resolving for everyone, and an alias given up takes somebody's bookmark with
it. An order that ends without a certificate is `warn`, because the member asks again and the name
keeps the certificate it already holds. The zone not answering is the one `danger`: no name can be
written and no certificate ordered behind it.

`DnsJournalTests` asserts the producer's own half — every method writes all three fields, every
declared name is lowercase, dotted and under `dns.`, and each change carries the weight its effect
deserves.

## [0.12.0]

### Added — a member renews a certificate when an admin asks

- **`DnsRenewHandler`**, registered by `AddKgsmDnsMember`, answers `dns.renew`: from the member holding
  `dns`, for a name this member holds published, it asks for that name's certificate at once through
  `CertificateKeeper.ForceAsync`. A renewal from any other member, or for a name not published here, is
  logged and dropped. "Renew now" on another member's name therefore produces a new certificate on that
  member rather than a journalled request nobody answered.
### Fixed — a renewed certificate is the one the web server answers with

A new certificate is written to the path the generated site already names, so the site itself does not
change and the web server goes on answering with the certificate it read into memory. `NginxSite` now
reloads when a name is proved to be served with any certificate other than the one held for it, and
proves it again after. A name that answers nothing is unreachable from the member itself, which a
reload does not change, so that one is reported as before.

- `TheKrystalShip.KGSM.Dns 0.2.0-dev.14`: `DnsRenewHandler`, and the reload a renewal needs.

## [0.11.0]

### Added — an admin can act on what the anchor holds

- **`POST /api/v1/cluster/dns/zone/check`** brings the next zone pass forward to now, rather than waiting
  out the reconcile interval.
- **`POST /api/v1/cluster/dns/certificates/{name}/renew`** asks for a certificate to be renewed ahead of
  its own schedule: this anchor's own `CertificateKeeper` is woken directly for one of its own names,
  and a `dns.renew` bus message is queued for any other member's. `404` for an unknown name, `409` for a
  name that takes no certificate (a game name or an alias) or is not yet published.
- **`POST /api/v1/cluster/dns/aliases`** (`{ "server", "label" }`) and
  **`DELETE /api/v1/cluster/dns/aliases/{name}`** name an existing, published game server under an
  additional label in the play base — the same entitlement a game name has, following the same node and
  released with it. A new `NameKind.Alias`, its own mark, and `DnsNameStatus.AliasOf` naming the server
  an alias follows.
- **Every write needs admin**, checked by `ClusterBearer.AuthorizeAsync` against this anchor's own copy
  of the accounts; the reads stay open to a viewer. A member standing by for the capability, or one with
  no zone and credential configured, refuses every write with a body naming why rather than touching the
  registry, the zone or a member. CORS now admits `POST` and `DELETE` and a `Content-Type` header.
- **New journal events**: `dns.zone.check_requested`, `dns.certificate.renew_requested`,
  `dns.alias.added`, `dns.alias.removed` — each recorded with the acting person as `provider:username`.
- **`DnsCertificateOrder.RenewsFrom`** names when a member will next ask for a certificate — the start of
  its last third, the same fraction `HeldCertificate.Due` renews on, computed once in
  `HeldCertificate.RenewsFrom` and read by both. `DnsCertificatesStatus.FailedLastWeek` counts orders that
  ended without a certificate in the last seven days. A certificate's `NotBefore` is now recorded at
  issuance, in a `not_before` column added to `certificate_orders` on an existing store; an order from
  before this version reports `RenewsFrom` null rather than guessing.
- `TheKrystalShip.KGSM.Dns 0.2.0-dev.12`: `NameKind.Alias`, `NameKinds.TakesCertificate`,
  `HeldCertificate.RenewsFrom`, `DnsMessageTypes.Renew` / `DnsRenew`, the new `DnsStatusRoutes` and
  `DnsAliasRequest`, and the new `DnsEvents` / `DnsEventPayloads`.

## [0.10.0]

### Added — a person can read what the anchor holds

- **`GET /api/v1/cluster/dns`** answers one `DnsStatus`: this member's standing, the zone's health, every
  name with its target, state, block reason, declared ports and port collisions, every member with where
  it said it is reached and what that resolved to, and every certificate order. Answered by the anchor
  itself, so the page needs no Control Panel API anywhere in the cluster.
- **`GET /api/v1/cluster/dns/events`** answers the newest lines of the anchor's own journal.
- **Both are verified against the cluster's session.** The anchor verifies the auth anchor's sessions
  against its published key, keeps its own copy of the cluster's accounts (snapshot on start, every
  change after) and reads the caller's tier from it on every request, and records sessions ended
  elsewhere. A viewer or above is admitted; an anchor that cannot yet verify answers `503`. CORS admits
  `AllowedOrigins`. New settings: `AllowedOrigins`, `AccountsPath`, `SessionsPath`.
- **An event journal at `/var/lib/kgsm-dns/events/`**: names published, moved, withdrawn, contested and
  recovered; certificates issued, failed and refused; the zone becoming unreachable and restored. A pass
  that fails partway still records what it already changed.
- **Every certificate order is recorded** when it ends, with the issued certificate's expiry, in the
  registry's file for 120 days. The issuer's weekly limit is counted against it. Orders finished before
  this version are not in it.
- **The last zone pass is kept in memory**: when, how long, whether it succeeded, the provider's error,
  what it changed and when the next is due.
- Port collisions resolve every member that stated a host, and this anchor's own, so members sharing a
  router are visible before either runs a game server.
- `TheKrystalShip.KGSM.Dns 0.2.0-dev.11`: `DnsStatus` and its parts with `DnsStatusJsonContext`,
  `DnsStatusRoutes`, `DnsEvents`, `DnsEventPayloads`.

## [0.9.0]

### Added — a member removed from the cluster loses its names

A member removed from the cluster is marked `left` in the roster. Once that row has stood for the settle
window — three minutes, or half the time a departed row is held if that is shorter — the anchor releases
the member's node name, its game names and any capability name following it, and forgets the host it
stated; the next zone pass withdraws the records.

- **Offline is not departed.** A dead member, and one reaped from the roster, keeps every name.
- **A refuted removal releases nothing.** A running member overturns its removal — measured at just over
  a minute — inside the settle window. One slower than that has its names released and states itself
  again the moment its incarnation rises: `TheKrystalShip.KGSM.Dns 0.2.0-dev.8` restates where a member
  is reached, and a node's game servers, on every rise.
- **Released once per removal**, recorded durably by the incarnation the `left` row carried, so a later
  tick or a restart does not release again. A member that comes back states itself and is named again
  at once, a pinned node under its pin, whether or not its refutation has arrived; a later removal
  carries a higher incarnation and is acted on.
- **A capability still assigned to it is withdrawn** rather than left pointing at a machine that is gone.
- **A member asks for its names as well as being told them.** The anchor's answer travels to a name
  the member serves, so a member serving none — one named again after a removal, or a node that has
  just joined — could never hear it. `GET /api/v1/dns/names` answers an authenticated member with its
  own names once it has said where it is reached, and a `409` before then, which the member treats as
  "not yet" and never as "none". The member asks for a minute after every statement, while the anchor
  has not answered and while a name waits for its record, and on the slow refresh otherwise
  (`TheKrystalShip.KGSM.Dns 0.2.0-dev.10`: `DnsRoutes`, `DnsNamesFetcher`).
- **A member serves a name waiting for its record.** The anchor answers a member that has just stated
  itself before the zone pass writes the record, and `IServedNames.Held` — published or pending — is what
  the site serves, while a certificate is asked for only once a name is published. A returning member
  keeps serving its name through that gap rather than withdrawing its site (`TheKrystalShip.KGSM.Dns
  0.2.0-dev.9`).
- Takes `TheKrystalShip.KGSM.Cluster 1.0.0-dev.21`, which holds a `left` row for seven days, so an anchor
  that was down when the removal was made still sees it when it returns.

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
