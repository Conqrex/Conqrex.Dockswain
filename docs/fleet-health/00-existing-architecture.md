# Existing Dockswain Architecture

This audit describes the repository before the Fleet Health work. It is the basis
for the monitoring design; the new layer must extend these paths rather than create
a separate application.

## Repository layout

- `package/` is the KDE Plasma 6 widget. `contents/ui/` contains QML views,
  `contents/code/dockswain.sh` is the local shell-to-SSH backend, and
  `contents/config/main.xml` is the KConfig schema.
- `macos/` is a native SwiftUI menu-bar application. It has its own bundled shell
  backend because authentication, process launching, and local filesystem APIs are
  platform-specific.
- `maui/` is a .NET MAUI application. It uses SSH.NET and SFTP directly rather than
  invoking either desktop shell helper.
- `packaging/`, `install.sh`, and `.github/workflows/` package and release the Plasma
  widget and macOS app. There is no shared runtime library between the three clients.

## Plasma architecture

`package/contents/ui/main.qml` is a thin `PlasmoidItem` controller. Server records
come from `Plasmoid.configuration.serversJson` and have the shape
`{label,user,host,port,key,auth,remmina,hasSecret,useSudo}`. The settings UI in
`configServers.qml` edits that JSON, imports Remmina/FileZilla profiles, and stores
passwords in Secret Service/KWallet through `secret-tool`; secrets are not written
to KConfig.

The root controller owns a persisted array of open server indexes. An
`Instantiator` creates one `ServerSession.qml` for every open tab. The root proxies
the active session's state and actions so `FullView.qml` and `CompactView.qml` do
not need to know about the session pool. Only open tabs are currently polled, which
is the key limitation for fleet-wide monitoring.

Each `ServerSession` owns an `executable` Plasma data engine, container and Compose
`ListModel`s, connection state, and timers. Its main timer runs the backend `list`
command for every open tab at `pollInterval`; Compose is refreshed with the list.
Stats use a separate `statsInterval` timer and are fetched only for the active tab
because `docker stats --no-stream` is relatively expensive. Filtering, network
grouping, and favorites are shared root state, while raw containers and stats are
per-session.

`FullView.qml` is the existing container-first popup. It contains the tab strip,
filter and actions, container/Compose lists, plus overlays for logs, disk cleanup,
nginx/certbot, file viewing, and the dual-pane file manager. `CompactView.qml`
currently reflects only the active session with a reachability dot and
running/total badge.

Settings are KConfig entries declared in `package/contents/config/main.xml` and
exposed by the two KCM QML pages. Existing settings include polling, optional stats,
Docker command, SSH timeout, Compose visibility, filters/favorites, nginx path,
terminal/editor, and file-manager behavior.

There is no Plasma event-history store or notification pipeline before this work.

## Plasma shell backend

`package/contents/code/dockswain.sh` runs locally and emits a normalized JSON object
for QML. Local-only commands handle host import, QR export, and filesystem work.
Remote commands build an SSH argv with a short connect timeout,
`StrictHostKeyChecking=accept-new`, keepalives, and a multiplexed control socket in
`$XDG_RUNTIME_DIR` (or a guarded per-user fallback). Key authentication is batch
mode; password authentication reads Secret Service and uses `sshpass -e` without
putting the password on the command line.

The helper classifies failures into stable reason codes. In particular it reports
SSH/network/auth failures with `reachable:false`, while Docker socket permission,
missing binary, or stopped daemon failures keep `reachable:true`. This is already
the correct foundation for distinguishing an offline server from Docker being
unavailable.

Docker container output is obtained with `docker ps -a --no-trunc --format
'{{json .}}'`, collected by local `jq`, and normalized to lower-case fields such as
`id`, `fullId`, `name`, `image`, `state`, `status`, `health`, `ports`, `networks`, and
`created`. Stats are similarly reduced to a map keyed by 12-character ID. Compose
projects and Swarm stacks are separate normalized arrays.

Disk data combines `df -PB1` for Docker's data root with `docker system df`; cleanup
is deliberately restricted to build cache, dangling images, and stopped containers.
Nginx discovery supports both `sites-available/sites-enabled` and `conf.d` layouts.
Certbot certificates are parsed into `{name,domains,expiry,valid}`. These checks are
currently requested only from per-server overlays and do not feed global health.

## macOS architecture

The macOS app stores server metadata and preferences in `UserDefaults`, passwords
in Keychain, and models each open tab as a `ServerSession`. `AppState` exposes an
active-session facade to SwiftUI, matching the Plasma pattern. Each open session
polls containers; only the active session polls stats. `Backend.swift` and
`Backend+Features.swift` call the bundled `dockswain-mac.sh`, whose normalized
contracts parallel the Linux helper while using Keychain/`SSH_ASKPASS`, macOS
temporary paths, and native local-file APIs.

`HealthMonitor.swift` already diffs lifecycle state between successful polls and
can notify for stopped/crashed, unhealthy/recovered, and restarting transitions.
It intentionally establishes the first poll as a silent baseline. However it sees
only open tabs, has no restart counter, thresholds, disk/image/certificate checks,
or persistent event history. The menu-bar item adds a warning marker if an open
session currently contains an unhealthy or restarting container.

## MAUI architecture

The MAUI client stores server metadata as JSON in MAUI `Preferences` and credentials
in `SecureStorage`. `RemoteShell` creates SSH.NET/SFTP connections and maps errors to
stable reason strings. `DockswainBackend` issues Docker/nginx/certbot commands and
returns typed models. `MainPage.xaml.cs` builds the UI in C# with feature tabs for
Containers, Compose, Disk, Files, Nginx, and Settings. A 12-second timer refreshes
only the selected server's container screen while it is visible.

MAUI already exposes container stats, Docker disk usage/cleanup, nginx, and raw
certificate expiry strings, but it has no background all-server polling, transition
detection, event store, alert settings, or fleet view.

## Compatibility constraints for Fleet Health

1. Interactive/open-tab sessions remain responsible for existing management
   screens and their current refresh behavior.
2. Fleet monitoring must use a separate lightweight session per configured server,
   so closing a UI tab does not stop operational monitoring.
3. Expensive metadata (disk, certificates, image state) needs a slower cadence than
   container health/resource samples.
4. The semantic field names and event kinds should be shared across clients, but
   persistence and notifications must remain native: KConfig/Plasma notifications,
   UserDefaults/UserNotifications, and MAUI Preferences/platform notifications.
5. First observations establish a baseline. Subsequent transitions create bounded,
   persistent events and notifications, preventing a fresh install from producing a
   fleet-wide alert storm.
6. Existing normalized JSON fields and shell subcommands remain compatible; new
   fields and commands are additive.
