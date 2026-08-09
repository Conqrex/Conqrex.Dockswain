# Fleet Health Operational Model

Fleet Health is an operational layer inside Dockswain, not a separate service or
application. It answers three questions: whether something is wrong anywhere in
the configured fleet, what changed, and which existing Dockswain action can address
it immediately.

## Collection model

Every configured server receives a monitoring session independently of the tabs or
screens the user has open. Existing interactive sessions still own container lists,
logs, shells, Compose, Swarm, SFTP, nginx, certbot, and cleanup actions.

Collection has two cadences:

- The fleet interval samples SSH reachability, Docker availability, container
  lifecycle/health/restart counters, optional container stats, host memory/load,
  and local Docker image identity. Its default is 30 seconds with a 10-second
  minimum.
- The deep interval samples Docker/data-root disk usage and certbot certificates.
  Its default is 15 minutes with a five-minute minimum.

The Linux and macOS shell helpers expose additive, normalized JSON commands. The
MAUI backend produces the corresponding typed model directly over SSH.NET. Existing
backend commands and management behavior are unchanged.

## Availability and severity

An SSH or network failure means the host is offline. If SSH succeeds but the Docker
binary, daemon, or socket cannot be used, the host is online and Docker is reported
as unavailable. Those are separate critical issues and separate recovery events.

Current issues are derived from the latest samples. Critical issues include offline
hosts, unavailable Docker, dead/unhealthy containers, expired certificates, and
very high disk use. Warnings include restart bursts, restarting/crashed containers,
threshold crossings, image drift, approaching certificate expiry, and ordinary
disk pressure.

Defaults are 85% CPU, memory, and disk, three restarts in 60 minutes, and 14 days
for certificate expiry. All can be changed without altering existing per-server
management settings.

## Events and notifications

The first observation of a server establishes a silent baseline. Later changes
create events for host and Docker loss/recovery, container creation/removal and
start/stop/crash, restart-count deltas, health transitions, threshold crossings and
recoveries, local image changes, disk pressure, and certificate expiry/renewal.

History is bounded and persistent:

- Plasma stores snapshots and events in the plasmoid's KConfig entries.
- macOS stores Codable snapshots and events in `UserDefaults`.
- MAUI stores serialized snapshots and events in `Preferences`.

The default limit is 250 events. Restart-window calculations use persisted restart
events, so closing and reopening the interface does not reset the recent count.
Desktop clients use their native notification channel. Recovery notifications are
optional. Mobile monitoring is foreground-only unless a future release adds an
explicit platform background service.

## Resource, disk, image, and SSL semantics

CPU thresholds apply to individual containers using Docker's per-core convention:
100% is one fully used CPU core, so a multi-threaded container can legitimately
exceed 100% (for example, 200% means two cores). Memory thresholds apply to both
containers and hosts. Disk pressure uses the Docker data-root filesystem and shows
reclaimable space reported by `docker system df`; cleanup remains an explicit,
confirmed existing action.

Image awareness does not contact a registry. For mutable tag references, Dockswain
compares the image ID used by the container with the image ID currently present for
that local tag. A mismatch means a newer/different image is already available
locally and the container has not been recreated from it. Digest-pinned references
are identified but not marked as updates. No background pull is performed.

SSL monitoring uses the existing certbot inventory and parses its expiry date.
Certificates are sorted by remaining days and link to the existing nginx/certbot
screen for action.

## Immediate actions

Fleet problems reuse current Dockswain operations. Container issues offer Restart
and Open actions; disk issues open cleanup; SSL issues open certificate management;
host and image rows open the existing container screen. Fleet Health adds awareness
and routing while keeping mutations behind the established commands and
confirmations.
