#!/bin/sh
# Prepares the integration test: Jellyfin's directories under $ROOT with the plugin
# installed and configured for three workers (default, intel, nvidia), and test media.
#
#   ROOT=/path PLUGIN_DLL=…/Jellyfin.Plugin.GrpcFfmpeg.dll sh prepare.sh
#
# ROOT is the path Jellyfin and the workers see. HOST_ROOT is where to write it, if that
# differs (e.g. a chroot); FFMPEG runs the ffmpeg that sees ROOT. WORKER_<NAME>_HOST/PORT
# set the worker addresses (default: hosts worker-default/-intel/-nvidia, port 50051).
set -eu
: "${ROOT:?ROOT must be set}" "${PLUGIN_DLL:?PLUGIN_DLL must be set}"
HOST_ROOT=${HOST_ROOT:-$ROOT}
FFMPEG=${FFMPEG:-/usr/lib/jellyfin-ffmpeg/ffmpeg}
TOKEN=${TOKEN:-integration}

mkdir -p "$HOST_ROOT/config/plugins/gRPC-ffmpeg_0.0.0.0" "$HOST_ROOT/config/plugins/configurations" \
         "$HOST_ROOT/cache/transcodes" "$HOST_ROOT/tmp" "$HOST_ROOT/logs" \
         "$HOST_ROOT/media/movies/H264 Movie (2020)" "$HOST_ROOT/media/movies/AV1 Movie (2021)"
cp "$PLUGIN_DLL" "$HOST_ROOT/config/plugins/gRPC-ffmpeg_0.0.0.0/"
cp "$(dirname "$0")/start-jellyfin.sh" "$HOST_ROOT/start-jellyfin.sh"

cat > "$HOST_ROOT/config/plugins/configurations/Jellyfin.Plugin.GrpcFfmpeg.xml" <<XML
<?xml version="1.0" encoding="utf-8"?>
<PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <Enabled>true</Enabled>
  <GrpcHost>${WORKER_DEFAULT_HOST:-worker-default}</GrpcHost>
  <GrpcPort>${WORKER_DEFAULT_PORT:-50051}</GrpcPort>
  <AuthToken>$TOKEN</AuthToken>
  <EnableFallback>true</EnableFallback>
  <Retries>2</Retries>
  <ConnectTimeout>3</ConnectTimeout>
  <EnableHardwareClasses>true</EnableHardwareClasses>
  <DefaultHardwareClass>auto</DefaultHardwareClass>
  <IntelClass>
    <Name>intel</Name>
    <Enabled>true</Enabled>
    <GrpcHost>${WORKER_INTEL_HOST:-worker-intel}</GrpcHost>
    <GrpcPort>${WORKER_INTEL_PORT:-50051}</GrpcPort>
    <!-- Wrong on purpose: the plugin detects the decoders and encoders (sim-ffmpeg.py) -->
    <HardwareDecodingCodecs><string>h264</string><string>hevc</string><string>vp9</string><string>av1</string></HardwareDecodingCodecs>
    <EnableDecodingColorDepth10Vp9>false</EnableDecodingColorDepth10Vp9>
    <AllowAv1Encoding>true</AllowAv1Encoding>
  </IntelClass>
  <NvidiaClass>
    <Name>nvidia</Name>
    <Enabled>true</Enabled>
    <GrpcHost>${WORKER_NVIDIA_HOST:-worker-nvidia}</GrpcHost>
    <GrpcPort>${WORKER_NVIDIA_PORT:-50051}</GrpcPort>
    <!-- VC-1 cannot be tested and must be kept -->
    <HardwareDecodingCodecs><string>h264</string><string>vc1</string></HardwareDecodingCodecs>
    <AllowAv1Encoding>false</AllowAv1Encoding>
  </NvidiaClass>
</PluginConfiguration>
XML

# H.264 1080p (decoded in hardware by both classes) and 10-bit AV1 (only by nvidia here)
$FFMPEG -hide_banner -loglevel error -y -f lavfi -i testsrc2=size=1920x1080:rate=24 -f lavfi -i sine=frequency=440 \
  -t 60 -c:v libx264 -preset ultrafast -g 48 -c:a aac -shortest "$ROOT/media/movies/H264 Movie (2020)/H264 Movie (2020).mkv"
$FFMPEG -hide_banner -loglevel error -y -f lavfi -i testsrc2=size=1280x720:rate=24 -f lavfi -i sine=frequency=440 \
  -t 60 -c:v libsvtav1 -preset 12 -pix_fmt yuv420p10le -g 48 -svtav1-params lp=2 -c:a aac -shortest \
  "$ROOT/media/movies/AV1 Movie (2021)/AV1 Movie (2021).mkv"

# Jellyfin and the workers may run as different users
chmod -R a+rwX "$HOST_ROOT"
echo "Prepared $ROOT"
