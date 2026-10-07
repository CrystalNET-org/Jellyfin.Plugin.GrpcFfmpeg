# Contributing to the gRPC-ffmpeg plugin

Bug reports, ideas and pull requests are welcome.

- **Bugs:** open an [issue](https://github.com/CrystalNET-org/Jellyfin.Plugin.GrpcFfmpeg/issues)
  with the steps to reproduce, the Jellyfin and plugin versions, and the relevant lines from
  the plugin's console and Jellyfin's log (search for `gRPC-ffmpeg`). Problems with the
  workers themselves belong to [grpc-ffmpeg](https://github.com/CrystalNET-org/grpc-ffmpeg/issues).
- **Pull requests:** against `main`. Keep them focused, and update the README when behaviour
  or settings change.

## Repository layout

```
Jellyfin.Plugin.GrpcFfmpeg/
├── Jellyfin.Plugin.GrpcFfmpeg/
│   ├── Plugin.cs                    # plugin entry, settings page registration
│   ├── PluginServiceRegistrator.cs  # makes Jellyfin's MediaEncoder use the client
│   ├── Relay.cs                     # unpacks the client, writes grpc-ffmpeg.conf
│   ├── ActivityConsole.cs           # reads the clients' activity log for the console
│   ├── HardwareClasses/             # experimental: per-session hardware classes
│   ├── Controllers/                 # API for the settings page (status, console, test)
│   ├── Configuration/               # plugin settings
│   └── Web/config.html              # settings page
├── Jellyfin.Plugin.GrpcFfmpeg.Tests/  # unit tests
├── tests/integration/               # end-to-end test in real Jellyfin images (CI)
├── images/                          # catalog image and README screenshots
├── manifest.json                    # plugin repository file for Jellyfin
├── scripts/
│   ├── next-release-tag.sh          # computes the next patch tag
│   └── update_manifest.py           # adds a release to manifest.json
├── .woodpecker/                     # CI pipelines
└── renovate.json                    # dependency updates
```

## Building

Needs the .NET 9 SDK.

```bash
dotnet build -c Release Jellyfin.Plugin.GrpcFfmpeg/Jellyfin.Plugin.GrpcFfmpeg.csproj
```

The output is `Jellyfin.Plugin.GrpcFfmpeg/bin/Release/net9.0/Jellyfin.Plugin.GrpcFfmpeg.dll`.

Unit tests:

```bash
dotnet test Jellyfin.Plugin.GrpcFfmpeg.Tests
```

The integration test (`.woodpecker/integration.yaml`) runs the plugin in the official Jellyfin
images, the latest release of each major version (10.11 and 12.2), with three grpc-ffmpeg
workers whose ffmpeg is `tests/integration/sim-ffmpeg.py`: it logs every command and runs
hardware transcodes in software, as CI has no GPU. `tests/integration/check.py` then plays
sessions through Jellyfin's API and checks which worker got which command, that seeks keep
their class, that Jellyfin's settings are unchanged and that the plugin reports no warnings. It
catches what unit tests cannot, such as a Jellyfin version that stops reading its settings per
request, and releases depend on it. To run it elsewhere, follow the steps of the pipeline:
`prepare.sh`, three workers with `BINARY_PATH_PREFIX` pointing at `tests/integration/bin/`,
`start-jellyfin.sh`, then `check.py`; all of them must see `ROOT` at the same path. Copy it
into a folder in Jellyfin's `plugins` directory and restart Jellyfin to try it.

The plugin builds against the Jellyfin 10.11 packages, so one build runs on 10.11 and 12.x.
Test changes on both when they touch Jellyfin's internals, such as the `MediaEncoder`
registration.

The build downloads the grpc-ffmpeg client binaries of the release set in
`GrpcFfmpegVersion` (in the `.csproj`) and embeds them. To embed local builds instead, pass
`-p:GrpcFfmpegAssetsDir=/path/to/dir/`. The directory must contain `grpc-ffmpeg-client-amd64`,
`grpc-ffmpeg-client-arm64` and optionally `grpc-ffmpeg-client-windows-amd64.exe`.

## CI

The pipelines in `.woodpecker/` run on [Woodpecker CI](https://woodpecker-ci.org/):

| Pipeline | Runs on | Does |
| --- | --- | --- |
| `build.yaml` | pushes to `main`, pull requests, manual | Builds the plugin and runs the unit tests |
| `integration.yaml` | pushes to `main`, pull requests, manual, tags | End-to-end test in Jellyfin 10.11 and 12.2 (see above) |
| `auto_release.yaml` | pushes to `main` that change the `.csproj` | Tags a patch release, after the build succeeded |
| `release.yaml` | tags | Builds the release zip, publishes the GitHub release and updates `manifest.json` |
| `renovate.yaml` | cron, manual | Runs Renovate |

## Releases

Push a tag such as `0.3.0`. CI builds the plugin as version `0.3.0.0`, publishes
`gRPC-ffmpeg_0.3.0.0.zip` (the DLL, `meta.json` and the plugin image) as a GitHub release,
and adds the release to `manifest.json` on `main`, with the commit titles since the previous
release as its changelog. That commit is marked `[CI SKIP]`. Jellyfin then offers the update
in its plugin catalog.

Dependency updates are released automatically:

1. Renovate opens PRs for new grpc-ffmpeg releases (one hour after the release, once its
   client binaries are published) and for Jellyfin package patch updates within 10.11.
2. The build pipeline builds the PR, and Renovate merges it once it passes.
3. On `main`, once the build succeeded, `auto_release.yaml` pushes the next patch tag if the
   embedded grpc-ffmpeg release or the Jellyfin packages differ from the latest release
   (`scripts/next-release-tag.sh`).

The catalog shows `images/thumb.png` through `imageUrl` in `manifest.json`; the release zip
contains the same image for the installed plugin's page.
