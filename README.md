# kgsm-dns

The names a KGSM cluster is reached at, and the one member that publishes them.

Every member, every capability and every game server has its own name under one domain, and a
machine is configured with none of them: a member joins knowing only where to ask, and is given a
name. One anchor holds the zone's credential and writes every record; every other member asks it.

```
<member>.nodes.example.com        a member — a browser and the other members reach it here
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
| member | `A` / `AAAA` | the public address observed for that member |
| capability | `CNAME` | the member currently holding the capability |
| game server | `CNAME` | the member's own name |

Every target is derived by the anchor from what the cluster already knows, never taken from the
request — so a member can only ever have names pointing at itself.

Every record carries a mark saying which cluster name it is and who holds it. A record without one
is never edited, deleted, or counted as satisfying a name: a zone holds records people made by hand,
and the name alone is not evidence of who made it. The marks are also what the registry is rebuilt
from, so a member taking the capability over converges on what the previous holder published with
nothing having moved between them.

## Packages

| package | contents | taken by |
|---|---|---|
| **`TheKrystalShip.KGSM.Dns`** | labels, the three bases, and the mark a published record carries. No dependencies, AOT-safe. | every cluster member |

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
