#!/usr/bin/env python3
"""ffmpeg for the integration test's workers, which have no GPU.

Logs every command (one JSON line per command in $SIM_LOG_DIR/$WORKER_NAME.log) and
runs it. Hardware HLS transcodes cannot run without the GPU, so they run as an
equivalent software transcode instead: same input, start time, segment numbers and
output files, at real-time speed, so Jellyfin gets real segments and its transcode
jobs stay alive like on real hardware.

REAL_FFMPEG is the ffmpeg to run, possibly with a prefix (e.g. "chroot /root ffmpeg");
default /usr/lib/jellyfin-ffmpeg/ffmpeg.
"""
import json
import os
import shlex
import sys

args = sys.argv[1:]
log_dir = os.environ.get("SIM_LOG_DIR")
if log_dir:
    record = {"worker": os.environ.get("WORKER_NAME", "?"), "args": args,
              "cuda_visible_devices": os.environ.get("CUDA_VISIBLE_DEVICES")}
    with open(os.path.join(log_dir, os.environ.get("WORKER_NAME", "worker") + ".log"), "a") as log:
        log.write(json.dumps(record) + "\n")

real = shlex.split(os.environ.get("REAL_FFMPEG", "/usr/lib/jellyfin-ffmpeg/ffmpeg"))


def option(name):
    return args[args.index(name) + 1] if name in args and args.index(name) + 1 < len(args) else None


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
