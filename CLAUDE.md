# CLAUDE.md — kgsm-dns

Guidance for working in this repo. The design's authority is `cluster-dns-plan.md` at the workspace
root; this file is how the code implementing it is laid out and what must stay true of it.

## What this is

One cluster anchor (`kgsm-dns`, Native AOT, systemd) holding the `dns` capability, plus one package
(`TheKrystalShip.KGSM.Dns`) that every member compiles against. The anchor is the only member holding
a credential for the zone. Every other member asks it for a name.

```
src/Dns/          the names themselves: labels, the three bases, the record mark
src/Dns.Anchor/   the daemon: registry, provider, reconciler, capability standing
```

## The invariants

These are the reason the design is safe. Breaking one is not a regression in a feature — it is the
component becoming something else.

- **A target is derived, never accepted.** `NameService` works out where every record points from
  what the cluster already knows: a member's observed address, the capability's assigned holder, the
  owner's member name. Nothing a member sends decides where a name resolves, which is what bounds a
  compromised member to pointing names at itself.
- **Nothing outside the three bases is written.** `ClusterBases.Owns` is the test, and it is a test
  on the name rather than a judgement about intent. The names people sign in through live at the top
  of the zone, outside every base.
- **A record without this cluster's mark is not this cluster's.** Never edited, never deleted, never
  counted as satisfying a name. A person is free to make a record that looks exactly like one of
  ours.
- **The registry is desired state; the zone is reconciled to it.** One direction. The single thing the
  provider is authoritative for is its own handle for a record.
- **Recovery happens once, before anything is claimed, on an empty registry.** Once a member has read
  its registry, empty means everything was released — which looks identical to an unbuilt registry
  and must not be answered the same way, or a released name would be rebuilt from the record it is
  about to withdraw. And it precedes every claim: a claim against an unread registry draws a new name
  for a member the zone already names, and the next pass withdraws the one it is reached at.
- **A name is sticky.** A member keeps it across restarts and address changes. Certificates, game
  names and people's bookmarks all name it.
- **Nothing is published against a guess.** A member with no observed address, or a label whose whole
  numbered series is held, is a blocked row carrying the reason. A name pointing at a guess is worse
  than a name that does not resolve.
- **Standing by means writing nothing.** Two members writing one zone would each reconcile the
  other's records away, so a member that is not the holder does not touch the provider at all.

## Conventions

- **Native AOT, so no reflection.** Wire shapes go through a source-generated `JsonSerializerContext`;
  storage is raw ADO `Microsoft.Data.Sqlite`. Publish must stay at zero ILC warnings:
  `dotnet publish src/Dns.Anchor/Anchor.csproj -c Release -r linux-x64`.
- **The settings file is the floor.** `kgsm-dns.settings.json` declares the whole surface, one key per
  `AnchorSettings` property and per `[ConfigField]`. A knob that exists in one place and not the others
  fails the build, and an environment variable naming a key the file does not declare sets nothing.
- **`AnchorSettings` is what was written; `AnchorOptions` is what the daemon runs on.** Clamping and
  defaulting happen in the projection between them.
- **The descriptor is generated, never edited.** `deploy/kgsm-dns.anchor.json` is rewritten by every
  build from `AnchorSettings` and `Descriptor.cs`. The `.anchor.json` suffix routes it to
  `/var/lib/kgsm/anchors/`, which is not where a node's leaves are scanned for.
- **Logging is `AddSystemdConsole()`** with the ecosystem's level convention.

## Testing

`dotnet test kgsm-dns.slnx -c Release`. The anchor's tests run the reconciler against an in-memory
zone that refuses a duplicate name the way the real provider does — a fake that allowed duplicates
would pass tests the live provider fails, and that refusal is the lock two claims for one name race
against.

## Version tracking

The version lives in `src/Dns.Anchor/Anchor.csproj` and is read from there by `deploy/version.sh`, the
package and the release workflow. `src/Dns/Dns.csproj` versions on its own schedule, as a published
package. A version bump ships with its `CHANGELOG.md` entry in the same commit, and that commit is
tagged `v<version>`.
