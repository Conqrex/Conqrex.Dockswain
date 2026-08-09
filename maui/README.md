# Dockswain Mobile

A .NET MAUI mobile client for controlling the same remote Docker/nginx servers as
the Plasma widget and macOS menu-bar app.

The mobile app uses SSH.NET inside the app instead of shelling out to `ssh`, `scp`,
`jq`, or desktop keyrings. Server metadata is stored in MAUI Preferences; passwords,
private keys, and private-key passphrases are stored in SecureStorage.

## Features

- Fleet Health across every configured host, with separate SSH-offline and
  Docker-unavailable states, container health/restart events, CPU/memory thresholds,
  disk pressure, SSL expiry, and read-only local image drift detection.
- Persistent bounded event history plus immediate restart and navigation actions.
  Fleet polling runs while the mobile app is in the foreground; this release does
  not register an OS background service or claim continuous background alerts.
- Server profiles with password or pasted private-key authentication.
- QR import from the Plasma widget: scan one server or an all-servers QR from mobile
  Settings. If the desktop QR includes secrets, they are stored into SecureStorage.
- Live container list with search, running-only filter, network grouping, CPU/memory
  stats, start/stop/restart/remove, logs, and non-interactive `docker exec`.
- Docker Compose project list with `up -d`, `down`, and reported compose files.
- Docker disk usage, `docker system df`, safe prune actions, JSON log sizes, and log
  truncation.
- Remote SFTP file manager: browse, create folders, rename, delete, edit text files,
  upload from the phone, and download/share files.
- Nginx site and `conf.d` management: list, enable/disable, edit, create reverse proxy
  or static site configs, run `nginx -t`, reload, list certificates, and issue certbot
  certificates.

Interactive terminal sessions are intentionally not embedded. The mobile `Exec`
action runs a one-shot command through `docker exec ... sh -lc` and shows the output.

Image awareness compares a running container's image ID with the image currently
available for the same local tag. Dockswain Mobile never pulls images automatically.

## Build

From the repository root:

```sh
dotnet build maui/Dockswain.Mobile/Dockswain.Mobile.csproj -f net10.0-android
```

Run on an attached Android device or emulator:

```sh
dotnet build maui/Dockswain.Mobile/Dockswain.Mobile.csproj -f net10.0-android -t:Run
```

The generated project also includes iOS, Mac Catalyst, and Windows targets when built
on supported host OSes with the corresponding MAUI workloads installed.

## Server Requirements

- SSH access from the device to the target host.
- `docker` available to the SSH user, or set the profile's Docker command to
  `sudo docker`.
- For nginx, certbot, and log-file truncation, connect as root or enable **Use sudo**
  and configure NOPASSWD for the required commands. The app uses `sudo -n`; it will not
  prompt for a sudo password over SSH.
