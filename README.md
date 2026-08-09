<p align="center">
  <img src="package/contents/icons/conqrex-dockswain.svg" alt="Dockswain" width="112">
</p>

<h1 align="center">Dockswain</h1>

<p align="center">
  <b>Your Docker fleet. One operational view.</b><br>
  Monitor and manage every Docker host over SSH from KDE Plasma, macOS, or mobile —
  fleet health, containers, Compose, Swarm, logs, files, nginx, SSL, and cleanup
  without building a separate monitoring stack.
</p>

<p align="center">
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-3b82f6?style=flat-square" alt="MIT License"></a>
  <img src="https://img.shields.io/badge/KDE-Plasma%206-1d99f3?style=flat-square&logo=kde&logoColor=white" alt="KDE Plasma 6">
  <img src="https://img.shields.io/badge/Runtime-Docker-2496ed?style=flat-square&logo=docker&logoColor=white" alt="Docker">
  <img src="https://img.shields.io/badge/Transport-SSH-64748b?style=flat-square&logo=gnubash&logoColor=white" alt="SSH">
  <a href="https://github.com/SancaK9/Conqrex.Dockswain/releases"><img src="https://img.shields.io/github/v/release/SancaK9/Conqrex.Dockswain?style=flat-square&color=8b5cf6" alt="Release"></a>
  <a href="https://github.com/SancaK9/Conqrex.Dockswain/stargazers"><img src="https://img.shields.io/github/stars/SancaK9/Conqrex.Dockswain?style=flat-square&color=eab308" alt="Stars"></a>
</p>

<p align="center">
  <a href="#-install">Install</a> ·
  <a href="#-setup">Setup</a> ·
  <a href="#-features">Features</a> ·
  <a href="#-fleet-health">Fleet Health</a> ·
  <a href="#-platforms">Platforms</a> ·
  <a href="#-development">Development</a>
</p>

---

## ✨ Features

| | |
| --- | --- |
| 🚦 **Fleet Health** | One overview for every configured host: online state, Docker availability, unhealthy containers, resource pressure, image drift, disk usage, and expiring certificates. |
| 🧭 **Immediate action** | Move directly from a problem to Restart, Containers, Disk Cleanup, nginx, or Certificates. Dockswain reports the issue and keeps the fix close. |
| 📦 **Container control** | Search, filter, group, pin, start, stop, restart, remove, inspect live CPU/memory usage, follow logs, and exec a shell. |
| 🧩 **Compose & Swarm** | View Compose projects, bring stacks up or down, inspect compose files, and see Docker Swarm stacks and services. |
| 🗄️ **Disk & image awareness** | Track Docker data-root pressure and reclaimable space, run confirmed cleanup actions, and detect running containers using an older local image. |
| 🌐 **nginx & SSL** | Browse and edit nginx sites, create reverse-proxy or static configurations, run `nginx -t`, reload, issue certificates, and monitor certbot expiry. |
| 📁 **Dual-pane files** | Browse local and remote files, edit remotely, drag and drop, compare folders, and transfer or sync through `rsync`/`scp`. |
| 🖥️ **Multi-host workspace** | Keep several servers open as tabs while the independent fleet monitor continues checking every configured server. |
| 🔔 **Operational history** | Persist important transitions across restarts and notify only when state changes—not on every successful poll. Optional recovery notifications are supported. |
| 🔐 **Keyring-only secrets** | Passwords live in KWallet/Secret Service or macOS Keychain, never in the widget configuration. SSH keys and agents are supported too. |
| 🧲 **Fast SSH reuse** | OpenSSH multiplexing keeps one connection warm, so later polls, actions, terminals, and transfers avoid repeated handshakes. |
| 📱 **Desktop-to-mobile setup** | Export one server or the complete fleet as a QR code and import it into Dockswain Mobile. Including credentials is explicit and optional. |

> Dockswain is intentionally not Grafana. It focuses on concise operational
> awareness: **is something wrong, what changed, and where is the action that fixes it?**

## 🚦 Fleet Health

Fleet Health runs independently from open management tabs and continuously checks
all configured servers.

| Signal | What Dockswain detects | Where it takes you |
| --- | --- | --- |
| Availability | SSH offline vs. Docker unavailable | Host/container view |
| Containers | Health changes, restarts, crashes, and restart bursts | Restart or open container |
| Resources | Container CPU/memory and host memory thresholds | Container details |
| Storage | Docker data-root pressure and reclaimable space | Disk Cleanup |
| Images | Running container differs from its current local mutable tag | Containers / Images |
| TLS | Certificate expiry from the existing certbot inventory | nginx / Certificates |
| History | Persistent transitions and optional recovery events | Fleet event timeline |

The first successful sample is a silent baseline. Later changes become bounded,
persistent events and can trigger native desktop notifications. Fast health checks
and slower disk/SSL checks have separate configurable intervals.

Docker reports container CPU per core: **100% is one fully used core**, so a
multi-threaded container can legitimately report 200% or more.

Image checks are local and read-only. Dockswain never pulls images in the
background and does not pretend to be a registry update service.

## 📦 Install

### From source

```sh
git clone https://github.com/SancaK9/Conqrex.Dockswain.git
cd Conqrex.Dockswain
./install.sh
```

Right-click your desktop or panel → **Add Widgets** → search **Dockswain**.

### Arch Linux / CachyOS

Add the hosted repository to `/etc/pacman.conf`:

```ini
[dockswain]
SigLevel = Optional TrustAll
Server = https://github.com/SancaK9/Conqrex.Dockswain/releases/download/arch-repo
```

Then install normally:

```sh
sudo pacman -Syu dockswain
```

Updates arrive with the rest of the system through `pacman -Syu`. The repository
is currently unsigned, which is why the entry uses `Optional TrustAll`.

<details>
<summary>Reload Plasma after upgrading a source install</summary>

```sh
kquitapp6 plasmashell && kstart plasmashell
```

</details>

Package id: `com.conqrex.dockswain`

## 🔑 Setup

1. Open widget settings → **Servers**.
2. Add a server manually, add the local machine, or import SSH targets from
   **Remmina** or **FileZilla**.
3. Choose **SSH key / agent** or **Password** authentication.
4. Confirm that the remote user can run `docker`, then open **Fleet Health**.

For a passphrase-protected SSH key, load it before starting Plasma:

```sh
eval "$(ssh-agent)"
ssh-add ~/.ssh/id_ed25519
```

Password authentication uses `sshpass`, but the password itself is retrieved from
KWallet/Secret Service at connection time and is never placed directly on the
command line. Imported Remmina passwords can be reused from the existing keyring.

To manage the local Docker daemon, **Add local** still connects through SSH to
`localhost`; an SSH server and non-interactive authentication must therefore be
available on the machine.

The remote account needs Docker access through the `docker` group, rootless Docker,
root, or the configured `sudo docker` command. Privileged nginx, certbot, and file
operations use `sudo -n`, so any required sudo rule must be `NOPASSWD`.

## 🖥️ Requirements

| Component | Purpose | Required |
| --- | --- | --- |
| KDE Plasma 6 | Hosts the Linux widget | ✅ |
| Bash, `jq`, OpenSSH | Normalized backend and SSH transport | ✅ |
| KDE Prison | QR-code rendering for mobile export | ✅ |
| Docker on each server | Container runtime being managed | ✅ |
| `sshpass` + `secret-tool` | Password authentication and secure storage | for password auth |
| `notify-send` | KDE fleet alerts | optional |
| `rsync` | Faster transfers, progress, compare, and sync | optional |
| Konsole / Kate | Interactive shells and external editing | optional |
| nginx / certbot | Web-server and certificate tools | optional, remote |
| Python 3 | FileZilla profile import | optional |

## 💻 Platforms

| Client | Experience | Credential storage | Monitoring model |
| --- | --- | --- | --- |
| **KDE Plasma 6** | Full panel/desktop widget | KWallet / Secret Service | Continuous while Plasma runs |
| **macOS 13+** | Native SwiftUI menu-bar app | macOS Keychain | Continuous while the app runs |
| **.NET MAUI** | Android-first mobile client with iOS, Mac Catalyst, and Windows targets | SecureStorage | Foreground fleet polling |

The clients share the same server and Fleet Health concepts while using native
storage, notifications, UI, and SSH integrations for each platform.

- Build the native app from [`macos/`](macos/) — see the
  [macOS guide](macos/README.md).
- Build or run the mobile client from [`maui/`](maui/) — see the
  [MAUI guide](maui/README.md).

## ⚙️ How it works

The Plasma UI calls [`package/contents/code/dockswain.sh`](package/contents/code/dockswain.sh),
which runs Docker and server-management commands over SSH and returns normalized
JSON to QML. The macOS app uses a corresponding native shell helper; MAUI uses
SSH.NET and typed models directly.

OpenSSH connections use a guarded per-user control socket with
`ControlMaster=auto` and `ControlPersist`, short connection timeouts, keepalives,
and non-interactive authentication. New host keys are accepted; changed keys are
rejected.

The implementation and monitoring contracts are documented in:

- [Existing architecture audit](docs/fleet-health/00-existing-architecture.md)
- [Fleet Health operational model](docs/fleet-health/01-operational-model.md)

## 🛠️ Development

Install and preview without replacing the running panel widget:

```sh
plasmoidviewer -a ./package -f planar -l floating
```

Or use the normal local test loop:

```sh
./install.sh
kquitapp6 plasmashell && kstart plasmashell
```

Useful checks:

```sh
bash -n install.sh package/contents/code/dockswain.sh
qmllint package/contents/ui/*.qml package/contents/config/config.qml
jq . package/metadata.json >/dev/null
xmllint --noout package/contents/config/main.xml
dotnet build maui/Dockswain.Mobile/Dockswain.Mobile.csproj -f net10.0-android
```

Releases, the hosted pacman repository, and AUR publishing are automated through
the workflows in [`.github/workflows/`](.github/workflows/). Add `#minor` or
`#major` to a release commit message to override the default patch bump; add
`[skip release]` to skip automation.

Contributions are welcome—issues and pull requests alike.

## 🛡️ Security

- Passwords are stored in the platform keyring, not KConfig or repository files.
- SSH polls are non-interactive and fail fast instead of opening hidden prompts.
- Destructive container, Compose, disk, and file operations retain confirmations.
- Background monitoring is read-only: it does not prune, pull, restart, reload, or
  renew anything automatically.
- Mobile QR exports exclude credentials unless you explicitly opt in.

## 🐙 Sibling project

[**Conqrex.OctoPulse**](https://github.com/Conqrex/Conqrex.OctoPulse) follows
GitHub Actions across your repositories and organizations from one KDE Plasma
widget, with live status, logs, re-run, cancel, and workflow dispatch.

## ☕ Support

If Dockswain saves you a few trips to a terminal, you can
[buy me a coffee](https://www.buymeacoffee.com/sancak). It is appreciated, never
expected.

## 📄 License

[MIT](LICENSE) © 2026 Serhan Aydinicen
