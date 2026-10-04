#!/usr/bin/env python3
"""ffmpeg for the integration test's workers, which have no GPU.

Logs every command received from Jellyfin (one JSON line per command in
$SIM_LOG_DIR/$WORKER_NAME.log) and runs it. Hardware HLS transcodes cannot run without the GPU, so they run as an
equivalent software transcode instead: same input, start time, segment numbers and
output files, at real-time speed, so Jellyfin gets real segments and its transcode
jobs stay alive like on real hardware.

The plugin's GPU test encodes (a few frames from lavfi with an _nvenc or _qsv encoder)
succeed for the codecs in SIM_GPU_ENCODERS, by default h264 and hevc on the intel worker
and h264, hevc and av1 on the nvidia worker, and fail like on a GPU without them otherwise.
Its GPU decoding tests (a clip on stdin for an _cuvid or _qsv decoder) succeed for the
clips in SIM_GPU_DECODERS, as found by decoding the clip in software: intel h264, hevc,
hevc10, vp9 and vp910, nvidia all of them.

REAL_FFMPEG is the ffmpeg to run, possibly with a prefix (e.g. "chroot /root ffmpeg");
default /usr/lib/jellyfin-ffmpeg/ffmpeg.
"""
import json
import os
import re
import shlex
import subprocess
import sys

args = sys.argv[1:]
log_dir = os.environ.get("SIM_LOG_DIR")
# The worker's own self-test (it converts its sample to grpc-ffmpeg-healthcheck-*.mp4,
# right at startup) is not a command from Jellyfin
self_test = any(os.path.basename(a).startswith("grpc-ffmpeg-healthcheck-") for a in args)
if log_dir and not self_test:
    record = {"worker": os.environ.get("WORKER_NAME", "?"), "args": args,
              "cuda_visible_devices": os.environ.get("CUDA_VISIBLE_DEVICES")}
    with open(os.path.join(log_dir, os.environ.get("WORKER_NAME", "worker") + ".log"), "a") as log:
        log.write(json.dumps(record) + "\n")

real = shlex.split(os.environ.get("REAL_FFMPEG", "/usr/lib/jellyfin-ffmpeg/ffmpeg"))


def option(name):
    return args[args.index(name) + 1] if name in args and args.index(name) + 1 < len(args) else None


GPU_ENCODERS = {"intel": "h264,hevc", "nvidia": "h264,hevc,av1"}
GPU_DECODERS = {"intel": "h264,hevc,hevc10,vp9,vp910", "nvidia": "h264,hevc,hevc10,mpeg2video,vp8,vp9,vp910,av1"}
encoder = option("-c:v") or ""

if "pipe:0" in args and encoder.endswith(("_cuvid", "_qsv")):
    # Which clip arrived: decode it in software, as the GPU would in hardware
    probe = subprocess.run(real + ["-hide_banner", "-f", "matroska", "-i", "pipe:0", "-frames:v", "3", "-f", "null", "-"],
                           stdin=sys.stdin.buffer, capture_output=True, text=True)
    match = re.search(r"Video: (\w+)", probe.stderr)
    clip = (match.group(1) if match else "?") + ("10" if "10le" in probe.stderr and match and match.group(1) != "av1" else "")
    supported = os.environ.get("SIM_GPU_DECODERS", GPU_DECODERS.get(os.environ.get("WORKER_NAME", ""), ""))
    if probe.returncode == 0 and clip in supported.split(","):
        sys.exit(0)
    print(f"[{encoder} @ 0x0] simulated GPU cannot decode {clip}: Codec not supported", file=sys.stderr)
    sys.exit(1)

if "-init_hw_device" in args and "lavfi" in args and encoder.endswith(("_nvenc", "_qsv")):
    supported = os.environ.get("SIM_GPU_ENCODERS", GPU_ENCODERS.get(os.environ.get("WORKER_NAME", ""), ""))
    if encoder.rsplit("_", 1)[0] in supported.split(","):
        sys.exit(0)
    print(f"[{encoder} @ 0x0] simulated GPU without {encoder}: unsupported device", file=sys.stderr)
    sys.exit(1)

if "-init_hw_device" in args and "-hls_segment_filename" in args:
    # Progress output on stderr as from the real command: Jellyfin follows it (10.11 waits
    # for it before serving the first segment), and the client sees the command start
    command = real + ["-hide_banner", "-re"]
    if option("-ss"):
        command += ["-ss", option("-ss")]
    command += [
        "-i", option("-i"), "-map", "0:v:0", "-map", "0:a:0?",
        "-c:v", "libx264", "-preset", "ultrafast", "-vf", "scale=640:-2",
        "-force_key_frames", "expr:gte(t,n_forced*3)", "-c:a", "aac",
        "-copyts", "-avoid_negative_ts", "disabled",
        "-f", "hls", "-hls_time", "3", "-hls_segment_type", "mpegts",
        "-start_number", option("-start_number") or "0",
        "-hls_segment_filename", option("-hls_segment_filename"),
        "-hls_playlist_type", "vod", "-hls_list_size", "0", "-y", args[-1],
    ]
else:
    command = real + args
os.execvp(command[0], command)
