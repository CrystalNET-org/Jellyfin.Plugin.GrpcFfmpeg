# gRPC-ffmpeg for Jellyfin

![gRPC-ffmpeg](images/thumb.png)

Run Jellyfin's ffmpeg on other machines. This plugin hands every ffmpeg and ffprobe command
Jellyfin runs to [grpc-ffmpeg](https://github.com/CrystalNET-org/grpc-ffmpeg) workers, for
example on the nodes of a cluster that have a GPU. That covers transcoding, probing, image
extraction and the ffmpeg calls of other plugins.

## Why

Jellyfin transcodes on the machine it runs on. In a cluster, that ties Jellyfin to the node
with the GPU, and that one node does all the transcoding. With this plugin, Jellyfin stays
where it is and needs no GPU: the work runs on workers on the GPU nodes, as many as you like
behind one address.

The plugin works with the official Jellyfin images and packages. No custom image, no
environment variables and no change to Jellyfin's ffmpeg path are needed.

## Features

- **Everything through the workers:** Jellyfin's own ffmpeg calls, and `ffmpeg`, `ffprobe`,
  `mediainfo` and `vainfo` calls of other plugins.
- **Transparent:** output, progress and exit codes arrive as if ffmpeg ran locally, and
  stopping a transcode stops it on the worker.
- **Fallback:** if no worker is reachable or the workers reject the token, commands run on
  the local ffmpeg, so Jellyfin keeps working and starting. The settings page and Jellyfin's log
  show when that happens.
- **Setup check:** one click tests the connection, checks that the workers share Jellyfin's
  directories, and warns about mismatched ffmpeg versions and overriding environment variables.
- **Live console:** every command with its exit code, duration and, for failures, ffmpeg's
  last error lines, including calls whose output Jellyfin discards.
- **Updates through the plugin catalog:** new grpc-ffmpeg releases are packaged automatically.

## Screenshots

The settings page, with a passing setup test: the workers are reachable and share Jellyfin's
directories.

![Settings page with the worker settings and a passing setup test](images/settings.png)

The console lists every command run through the workers, here a library scan's probe, an
image extraction and a transcode:

![Console with ffprobe, image extraction and transcode commands](images/console.png)

## Requirements

- **Jellyfin** 10.11 or 12.x on Linux (amd64 or arm64). A Windows (amd64) client is included
  but not yet tested with Jellyfin.
- **grpc-ffmpeg workers** reachable from Jellyfin. Their
  [README](https://github.com/CrystalNET-org/grpc-ffmpeg#getting-started) shows how to run
  them. The workers' ffmpeg must have the same major version as Jellyfin's own:

  | Jellyfin | Worker image |
  | --- | --- |
  | 12.x | `8.1.3-7.8` or later |
  | 10.11 | `7.1.4-7.7` |

- **Shared paths:** the workers read and write Jellyfin's files directly, so these must be
  mounted at the same paths on Jellyfin and on every worker (e.g. over NFS):
  - the media library,
  - Jellyfin's cache directory, including the transcode directory,
  - Jellyfin's temp directory, `/tmp/jellyfin` by default, where image extraction and trickplay
    write their output. Mount a shared volume there on Jellyfin and on the workers.

  Mount them the same way everywhere: the same protocol and the same mount options. File names
  must look identical on Jellyfin and on the workers, as Jellyfin sends its own paths (see
  *Troubleshooting* for SMB/CIFS shares).
- **One kind of GPU:** all workers need the same kind of GPU (e.g. all Intel QSV), matching
  the hardware acceleration set in Jellyfin.

## Installation

1. In Jellyfin, open *Dashboard → Plugins → Manage Repositories* and add this repository:
   ```
   https://raw.githubusercontent.com/CrystalNET-org/Jellyfin.Plugin.GrpcFfmpeg/main/manifest.json
   ```
2. Install **gRPC-ffmpeg** from the catalog and restart Jellyfin.
3. Open **gRPC-ffmpeg** in the dashboard sidebar, below *Plugins*. Enter the worker's host,
   port and token (the worker's `VALID_TOKEN`), then click **Test**, which tests the values as
   entered, before saving them. The test also has the workers read and write test files in
   Jellyfin's transcode and temp directories and probe a file of each library, and shows what
   is not shared yet.
4. Tick **Use gRPC workers for ffmpeg**, save, and restart Jellyfin.

After the restart, the status on the settings page shows that Jellyfin runs ffmpeg through
the plugin. Jellyfin's log shows `FFmpeg: <data dir>/grpc-ffmpeg/ffmpeg`, and
`Found ffmpeg version` reports the workers' version.

To install without the catalog, extract a
[release](https://github.com/CrystalNET-org/Jellyfin.Plugin.GrpcFfmpeg/releases) zip into a
folder in Jellyfin's `plugins` directory.

## Settings

| Setting | Default | Description |
| --- | --- | --- |
| Host, Port | `ffmpeg-workers`, `50051` | Address of the worker, or of the Service or load balancer in front of the workers. |
| Authentication token | | The workers' `VALID_TOKEN`. |
| Use TLS, CA certificate path | off | Connect over TLS, verifying the worker with this certificate. |
| Run commands locally when no worker is reachable | on | The fallback, also used when the workers reject the token. Recommended, because Jellyfin does not start if its ffmpeg check fails. |
| Local ffmpeg directory | *(detected)* | ffmpeg for the fallback. Empty means the ffmpeg Jellyfin would use without the plugin. |
| Attempts before giving up | `2` | Attempts while no worker is reachable or all are busy, waiting 1, 2, 4 and then 5 seconds between them. Lower values make the fallback start sooner. |
| Connection timeout | `5` s | Time to wait for a connection per attempt. |
| Use gRPC workers for ffmpeg | off | Makes Jellyfin use the workers. Takes effect after a restart. |

All settings except the last apply to the next command, without a restart.

### Experimental: hardware classes

Jellyfin supports one hardware acceleration type, so all workers behind one address need the
same kind of GPU. With **hardware classes** (off by default; turning it on or off needs a
restart), one Jellyfin uses an Intel QSV pool and an NVIDIA NVENC pool at the same time, each
behind its own address. Ticking it on the settings page shows the class settings, with a tab
each for the default workers, Intel QSV and NVIDIA NVENC:

- Each new playback session gets a class when Jellyfin first reads its transcoding settings
  (after authentication). With the default **Automatic** policy:
  1. classes whose workers were just found unreachable are skipped,
  2. of the rest, classes that can decode the video in hardware (by their decoding codecs and
     10-bit settings, e.g. AV1 only on one class) are preferred, if any can,
  3. then the class with the fewest running transcodes per weight (Jellyfin's own transcode
     jobs, plus sessions assigned in the last 30 seconds),
  4. then a class that can encode the codec the client prefers (e.g. AV1),
  5. and otherwise the classes take turns.

  Other policies: take turns, a fixed class, or none. The session keeps its class for seeks
  and later segments (remembered for 6 hours of inactivity). It only moves when its class's
  workers are unreachable and it has no running transcode, e.g. when the player retries. A
  session that got Jellyfin's own settings because no class was usable gets a class the same
  way, once one is.
- During that session's streaming requests, Jellyfin sees its transcoding settings with the
  class's hardware acceleration type, decoding codecs, tone mapping and HEVC/AV1 encoding
  settings. Jellyfin's saved settings are not changed.
- The client sends each command to the pool its hardware arguments need (`CLASS_ADDRESSES`).
  Everything else, such as startup checks, library scans, trickplay and image extraction, uses
  Jellyfin's own settings and the worker above. Like the switch itself, the addresses only
  change with a restart.
- **Decoders and encoders are detected**: on startup and whenever the settings are saved, each
  enabled class's workers
  - encode a few test frames with H.264, HEVC and AV1 on their GPU, set up like Jellyfin's own
    commands (the ffmpeg build lists every encoder, so only a real encode tells), and
  - make short test clips in software (H.264, HEVC and VP9 in 8 and 10 bit, MPEG-2, VP8, AV1)
    and decode them with the GPU's decoder (`*_cuvid`, `*_qsv`), which fails instead of
    falling back to software.

  What works becomes the class's encoding, hardware decoding and 10-bit decoding settings.
  VC-1 can't be tested (no software encoder) and is set by hand. Workers that can't be
  reached, or whose GPU doesn't work, leave the settings as they were, and so does a decoding
  test that can't even decode H.264.
- Tone mapping is on by default for each class, as the class's settings replace Jellyfin's.
- Each class's tab shows its workers' last check and, while active, its sessions and routed
  commands. Its **Test** button checks the workers as entered: that they answer, which codecs
  their GPU decodes and encodes, and that they share the transcode directory and can read the
  media.

Limitations: only QSV and NVENC (no VAAPI/AMD classes). Jellyfin's dashboard still shows its
global settings. Unreachable workers are only noticed through failed commands (with the
fallback enabled), and a running transcode is not moved. The feature
depends on Jellyfin reading the transcoding settings for each request, which is true for
10.11 and 12.1 but is not a public API.

## How it works

- On every start, the plugin unpacks the grpc-ffmpeg client it contains to
  `<data dir>/grpc-ffmpeg/` (e.g. `/config/grpc-ffmpeg/` in the official image), installs it
  as `ffmpeg`, `ffprobe`, `mediainfo` and `vainfo`, and writes the settings to the client's
  `grpc-ffmpeg.conf` next to it.
- Jellyfin takes its ffmpeg path from `--ffmpeg` / `JELLYFIN_FFMPEG`, which the official
  images set, so a plugin cannot change it through the settings. Instead, when activated, the
  plugin creates Jellyfin's ffmpeg service itself, with the path pointing at the client. When
  not activated, Jellyfin behaves as if the plugin was not installed.
- **Fallback:** Jellyfin checks ffmpeg at startup and does not start if that fails. With the
  fallback, a command runs on the local ffmpeg when no worker is reachable or the workers
  reject the token, so Jellyfin starts, and library scans and direct play keep working. Transcodes
  that need the workers' hardware acceleration fail until the workers are back. After a command
  found no worker, commands in the next 20 seconds go to the fallback right away. While commands
  fall back, the status on the settings page shows a warning with the reason, and Jellyfin's log
  has a warning; both clear once a command runs on the workers again.
- Jellyfin detects the ffmpeg version at startup, from the workers or, if they are down, from
  the local ffmpeg, and picks ffmpeg options for that version. That is why the workers and
  the local ffmpeg need the same major version.

## Console

The console on the settings page shows every command run through the client: its exit code,
its duration, the client's own messages (retries, authentication errors, fallback) and the
last lines of ffmpeg's error output for failed commands. It keeps the last 1000 lines in
memory.

On Linux the clients write to a named pipe (`console.fifo` in the client directory) that the
plugin reads, so nothing is written to disk. While Jellyfin is not reading it, the clients
drop their lines without waiting. On Windows they write to `grpc-ffmpeg.log`, rotated at 1 MB.

## Switching from a custom image

If Jellyfin runs in an image that already contains a grpc-ffmpeg client:

- **Remove the client's environment variables** (`GRPC_HOST`, `AUTH_TOKEN`, …) from the
  container. Environment variables override the client's config file, so they would take
  precedence over the plugin's settings. The status on the settings page lists any that are
  set.
- **Set the local ffmpeg directory** on the settings page to the real local ffmpeg, e.g.
  `/usr/lib/jellyfin-ffmpeg`, as long as the image's client is still Jellyfin's ffmpeg path.
  Otherwise the fallback runs that client, which tries the workers again.

## Troubleshooting

Check the console on the settings page first. Jellyfin also logs the plugin's decisions at
startup (search its log for `gRPC-ffmpeg`), and the workers log every command and every
rejected call.

- **The test fails with `Unauthenticated: Invalid token`:** the token does not match the
  worker's `VALID_TOKEN`.
- **The test fails with `Unavailable`:** the host or port is wrong, or the worker is not
  reachable from Jellyfin.
- **The test reports a directory as not shared:** the workers do not see that directory at
  the same path, see Requirements. Fix it before activating the plugin, or the commands
  writing there fail.
- **The test warns about the ffmpeg version:** use workers with the same ffmpeg major version
  as Jellyfin's local ffmpeg, see Requirements.
- **The status says a restart is required:** the activation changed since Jellyfin started.
- **Commands fail with "No such file or directory":** a path is not shared; run the test to
  see which.
- **Only some files fail with "No such file or directory", with paths like
  `/media/IG8JNE~S/I1RW5M~2.MP4`:** these are mangled 8.3 short names. A Samba (SMB/CIFS) share
  invents them for names with characters Windows does not allow (`: ? * " < > | \`, a trailing
  dot or space), which is common for e.g. anime releases. Jellyfin's mount shows the mangled
  names while the workers' mount shows the real ones (or the other way around), because the
  two mount the share differently. The test does not catch this, as it probes only one file
  per library. Compare `ls` of the directory in both, and mount the share identically:
  - NFS on both, if the server offers it (no name mangling at all), or
  - CIFS on both with the same options, including `mapposix` (or `mapchars`), which maps those
    characters instead of mangling the names.

  Then rescan the library, so that Jellyfin replaces the mangled paths it stored. Renaming the
  files to drop those characters works too.
- **The console shows `exit 1` for `-init_hw_device` commands at startup:** that is how
  Jellyfin checks VAAPI and Vulkan. These commands have no input or output, so ffmpeg always
  exits with 1. Jellyfin reads the driver details from their error output.

## Contributing

Bug reports and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for how to
build the plugin and how releases are made.
