# Dockswain

Dockswain is a KDE Plasma 6 widget for managing Docker hosts over SSH.

It gives you a live container dashboard from your panel or desktop: container
start/stop/restart, logs, shell exec, Compose projects, Docker disk cleanup,
nginx site management, certbot SSL actions, and a dual-pane SFTP file manager.

The repository also includes:

- A native macOS menu-bar app in [`macos/`](macos/).
- A .NET MAUI mobile app in [`maui/`](maui/).

## Highlights

- Multi-server tabs with live refresh.
- SSH key or password authentication.
- Passwords stored in KWallet/keyring, not in config files.
- Remmina and FileZilla import.
- Container search, running-only filter, network grouping, and pinned favorites.
- Inline logs with live follow.
- `docker compose` project controls and Swarm stack visibility.
- Docker disk usage and safe cleanup actions.
- nginx config browser, editor, test/reload actions, site creation, and certbot
  certificate setup.
- Dual-pane local/remote file manager over SFTP.
- Drag/drop uploads, `rsync`/`scp` transfer queue, folder compare, and sync.
- Optional CPU/memory stats.
- QR export for Dockswain Mobile.

## Install

Install or upgrade the Plasma widget for your user:

```sh
git clone https://github.com/SancaK9/Conqrex.Dockswain.git
cd Conqrex.Dockswain
./install.sh
```

Then right-click the desktop or panel, choose **Add Widgets**, and search for
**Dockswain**.

After changing local code, reload Plasma:

```sh
kquitapp6 plasmashell && kstart plasmashell
```

Package id:

```text
com.conqrex.dockswain
```

## Install With Pacman

Arch and CachyOS users can install from the GitHub-hosted pacman repository.

Add this to `/etc/pacman.conf`:

```ini
[dockswain]
SigLevel = Optional TrustAll
Server = https://github.com/SancaK9/Conqrex.Dockswain/releases/download/arch-repo
```

Then install:

```sh
sudo pacman -Syu dockswain
```

Updates arrive through normal pacman upgrades:

```sh
sudo pacman -Syu
```

The repository is currently unsigned, so the repo entry uses
`SigLevel = Optional TrustAll`.

## Authentication

Dockswain talks to each server over SSH. Server authentication is configured per
server under **Settings -> Servers**.

Password authentication uses `sshpass`, with the password stored in your
KWallet/keyring through `secret-tool`. The password is never written into the
plasmoid config and never placed directly on a command line.

SSH key authentication expects the key to be non-interactive. Use an unencrypted
key or load a passphrase-protected key into `ssh-agent`:

```sh
eval "$(ssh-agent)"
ssh-add ~/.ssh/id_ed25519
```

The remote user must be able to run Docker. Use a user in the `docker` group,
rootless Docker, root, or configure the Docker command as `sudo docker`.

## How It Works

The Plasma UI calls:

```text
package/contents/code/dockswain.sh
```

That helper runs Docker, nginx, certbot, and file commands over SSH and prints
normalized JSON back to QML.

SSH polling is non-interactive and multiplexed:

```sh
ssh -o BatchMode=yes -o ControlMaster=auto -o ControlPersist=60 user@host \
  'docker ps -a --no-trunc --format "{{json .}}"'
```

The first connection stays warm and later polls/actions reuse it.

## Using Dockswain

Common workflows:

- Add servers manually or import them from Remmina/FileZilla.
- Open multiple servers as tabs in the popup.
- Filter containers by name, image, or state.
- Start, stop, restart, remove, pin, or open logs for a container.
- Exec into a container in Konsole.
- Follow logs inline or in an external terminal.
- Bring Compose projects up/down and inspect compose files.
- View Docker disk usage and prune stopped containers, dangling images, or build
  cache after confirmation.
- Browse nginx config, edit sites, run `nginx -t`, reload nginx, and request
  certbot SSL certificates.
- Move files between local and remote panes, compare folders, and sync changes.
- Export server settings as QR codes for Dockswain Mobile.

Remote editing pulls a file into a temporary local copy, opens it in your
configured editor, and writes it back over SSH on save/close.

Privileged nginx/certbot/config actions need either a root login or **Use sudo**
enabled for the server. Since the widget cannot prompt for a sudo password, sudo
must be usable with `sudo -n` for those commands.

## Settings

Important settings:

- **Servers**: add/remove servers, choose password or key auth, import from
  Remmina/FileZilla, configure sudo, and export mobile QR codes.
- **General**: refresh intervals, stats polling, Docker command, terminal,
  confirmation prompts, SSH timeout, time format, default filters, nginx
  directory, editor, transfer tool, popup size, and file-manager behavior.

## macOS App

The native macOS app lives in [`macos/`](macos/). It is a SwiftUI menu-bar app
using the same SSH/Docker backend approach.

Build locally:

```sh
cd macos
./build-app.sh
```

See [`macos/README.md`](macos/README.md) for details.

## Mobile App

The MAUI app lives in [`maui/`](maui/). It uses in-app SSH/SFTP for the same
Docker, Compose, nginx, certbot, and file-manager workflows.

See [`maui/README.md`](maui/README.md) for setup details.

## Release Workflows

Releases are automated with GitHub Actions.

Every push to `main` runs `.github/workflows/release.yml` unless the head commit
message contains `[skip release]`. The workflow:

- bumps `package/metadata.json` and `packaging/aur/PKGBUILD`,
- commits the version bump back to `main`,
- creates a `vX.Y.Z` tag,
- calls `.github/workflows/pacman-repo.yml`,
- calls `.github/workflows/aur-publish.yml`.

Version bump defaults to patch. Use `#minor` or `#major` in the head commit
message to bump a larger version.

The pacman workflow publishes a fixed `arch-repo` GitHub Release containing:

- `dockswain.db`
- `dockswain.files`
- `*.pkg.tar.zst`

The AUR workflow is safe before AUR credentials exist. It exits cleanly until
the `AUR_SSH_PRIVATE_KEY` repository secret is configured.

Manual release runs are available from the Actions tab.

## Development

Useful checks:

```sh
bash -n install.sh package/contents/code/dockswain.sh
jq . package/metadata.json >/dev/null
xmllint --noout package/contents/config/main.xml
```

Preview with `plasma-sdk`:

```sh
plasmoidviewer -a ./package -f planar -l floating
plasmoidviewer -a ./package -f horizontal -l topedge
```

Or install and run windowed:

```sh
./install.sh
plasmawindowed com.conqrex.dockswain
```

## Security Notes

- Passwords are stored in KWallet/keyring and read at connection time.
- Remmina imports reuse the password Remmina already stored.
- `StrictHostKeyChecking=accept-new` accepts new hosts but rejects changed host
  keys.
- Shells open in external Konsole because Plasma QML widgets do not embed a real
  interactive terminal.
- Destructive actions are confirmed when confirmation prompts are enabled.

## Support

If Dockswain saves you a few trips to a terminal, you can buy me a coffee. It is
not expected, but it is appreciated.

[![Buy Me A Coffee](https://www.buymeacoffee.com/assets/img/custom_images/orange_img.png)](https://www.buymeacoffee.com/sancak)

## License

MIT
