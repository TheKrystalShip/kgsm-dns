# Changelog

All notable changes to `kgsm-dns` are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
