# Security

## Reporting a vulnerability

Please report security problems privately through
[GitHub's vulnerability reporting](https://github.com/CrystalNET-org/Jellyfin.Plugin.GrpcFfmpeg/security/advisories/new),
not as a public issue. Include what is affected, how to reproduce it, and the Jellyfin and
plugin versions. Problems in the workers or the client belong to
[grpc-ffmpeg](https://github.com/CrystalNET-org/grpc-ffmpeg/security/advisories/new).

You will get an answer as soon as possible. Fixes are released as a new plugin version and
announced in a security advisory.

## Supported versions

Only the latest release receives security fixes.

## What the plugin protects

- The plugin's API (status, console, test) is available to Jellyfin administrators only.
- The client's config file contains the workers' token and is readable by Jellyfin's user only.
- The console is kept in memory; on Linux it is passed through a named pipe readable by
  Jellyfin's user only.

The connection to the workers is protected by their token and, if enabled, TLS. See the
[security notes of grpc-ffmpeg](https://github.com/CrystalNET-org/grpc-ffmpeg/blob/main/SECURITY.md).
