#!/usr/bin/env python3
"""End-to-end check of the hardware classes against a running Jellyfin.

Expects Jellyfin prepared by prepare.sh and three workers whose ffmpeg is
sim-ffmpeg.py (default, intel, nvidia). Plays sessions through Jellyfin's API and
checks, from the commands the workers received and the plugin's status, that:

- Jellyfin's startup checks and library scan go to the default worker only, and the
  class workers only get the plugin's checks of their GPU encoders,
- the plugin detects each class's decoders and encoders and corrects the saved settings,
- the "auto" policy picks nvidia for 10-bit AV1 (only nvidia decodes AV1), the less
  loaded class next, and the class that encodes the client's preferred codec on a tie,
- each session's transcode carries its class's hardware arguments and reaches its
  class's worker only, also after a seek,
- Jellyfin's saved transcoding settings are unchanged,
- the plugin reports no warning (the silent failure modes) and no fallback.

Environment: JELLYFIN_URL (default http://jellyfin:8096), SIM_LOG_DIR (the workers'
command logs). Exits non-zero on failure.
"""
import json
import os
import sys
import time
import urllib.error
import urllib.request

BASE = os.environ.get("JELLYFIN_URL", "http://jellyfin:8096").rstrip("/")
LOG_DIR = os.environ["SIM_LOG_DIR"]
AUTH = 'MediaBrowser Client="integration", Device="check", DeviceId="integration-check", Version="1.0"'
PLUGIN_ID = "5FCE29C6-1366-41CD-9B05-6447A531B590"
# What sim-ffmpeg.py's GPUs encode
EXPECTED_ENCODERS = {"intel": ["h264", "hevc"], "nvidia": ["h264", "hevc", "av1"]}
EXPECTED_DECODERS = {"intel": ["h264", "hevc", "hevc10", "vp9", "vp910"],
                     "nvidia": ["h264", "hevc", "hevc10", "mpeg2video", "vp8", "vp9", "vp910", "av1"]}
# The saved settings that follow from them; nvidia keeps VC-1, which cannot be tested
EXPECTED_CODECS = {"intel": ["h264", "hevc", "vp9"], "nvidia": ["h264", "hevc", "mpeg2video", "vc1", "vp8", "vp9", "av1"]}
WORKERS = ("default", "intel", "nvidia")
token = None
failures = []


def call(method, path, body=None, raw=False, timeout=60):
    headers = {"Content-Type": "application/json", "Authorization": AUTH + (f', Token="{token}"' if token else "")}
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(BASE + path, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            payload = response.read()
            return response.status, payload if raw else (json.loads(payload) if payload else None)
    except urllib.error.HTTPError as e:
        return e.code, e.read()[:500]
    except Exception as e:  # connection refused while starting, timeouts
        return -1, str(e)


def get(obj, name):
    """Status fields as the plugin returns them (PascalCase), or camelCase."""
    return obj.get(name, obj.get(name[0].lower() + name[1:])) if isinstance(obj, dict) else None


def check(condition, message):
    print(("  ok    " if condition else "  FAIL  ") + message, flush=True)
    if not condition:
        failures.append(message)
    return condition


def commands(worker):
    try:
        with open(os.path.join(LOG_DIR, worker + ".log")) as log:
            return [json.loads(line)["args"] for line in log if line.strip()]
    except FileNotFoundError:
        return []


def counts():
    return {w: len(commands(w)) for w in WORKERS}


def new_commands(before):
    return {w: commands(w)[before[w]:] for w in WORKERS}


def is_gpu_check(args):
    """The plugin's check of a class's GPU: its ffmpeg version, or a test encode from lavfi."""
    return args in (["-hide_banner", "-version"], ["-version"]) or "lavfi" in args or "pipe:0" in args


def has_arg(args, prefix):
    return any(a.startswith(prefix) for a in args)


def wait_for(description, predicate, timeout=180, interval=2):
    deadline = time.time() + timeout
    while time.time() < deadline:
        result = predicate()
        if result:
            return result
        time.sleep(interval)
    sys.exit(f"Timed out waiting for {description}")


def setup():
    global token
    def started():
        # While starting, newer Jellyfin versions answer with a placeholder page (503)
        status, info = call("GET", "/System/Info/Public")
        return info if status == 200 and isinstance(info, dict) and info.get("Version") else None

    info = wait_for("Jellyfin to start", started, timeout=300)
    print(f"Jellyfin {info.get('Version')}", flush=True)
    if not info.get("StartupWizardCompleted"):
        call("POST", "/Startup/Configuration", {"UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"})
        call("GET", "/Startup/User")
        call("POST", "/Startup/User", {"Name": "admin", "Password": "admin"})
        call("POST", "/Startup/RemoteAccess", {"EnableRemoteAccess": True, "EnableAutomaticPortMapping": False})
        call("POST", "/Startup/Complete")
    status, auth = wait_for("the login", lambda: (lambda r: r if r[0] == 200 else None)(
        call("POST", "/Users/AuthenticateByName", {"Username": "admin", "Pw": "admin"})), timeout=60)
    if status != 200:
        sys.exit(f"Login failed: {status} {auth}")
    token = auth["AccessToken"]
    user_id = auth["User"]["Id"]
    status, folders = call("GET", "/Library/VirtualFolders")
    if not folders:
        media = os.environ.get("MEDIA_DIR") or os.path.join(os.environ["ROOT"], "media")
        call("POST", "/Library/VirtualFolders?name=Movies&collectionType=movies&refreshLibrary=true",
             {"LibraryOptions": {"PathInfos": [{"Path": os.path.join(media, "movies")}]}})

    refreshed = [0.0]

    def items():
        # Adding a library does not always start a scan; ask for one now and then
        if time.time() - refreshed[0] > 20:
            call("POST", "/Library/Refresh")
            refreshed[0] = time.time()
        status, result = call("GET", f"/Items?userId={user_id}&recursive=true&includeItemTypes=Movie&fields=MediaStreams")
        # Names may keep the year ("H264 Movie (2020)") when metadata providers are offline
        found = {}
        for item in result.get("Items", []) if status == 200 else []:
            for name in ("H264 Movie", "AV1 Movie"):
                if item["Name"].startswith(name) and any(s.get("Type") == "Video" for s in item.get("MediaStreams", [])):
                    found[name] = item
        return found if len(found) == 2 else None

    return wait_for("the library scan", items)


def play(item, session, codecs="h264", segment=0):
    """Requests the session's HLS playlist and one segment, like a player starting or seeking."""
    query = (f"?MediaSourceId={item['Id']}&PlaySessionId={session}&DeviceId=dev-{session}&VideoCodec={codecs}"
             "&AudioCodec=aac&VideoBitrate=2000000&AudioBitrate=128000&MaxWidth=1280&SegmentContainer=ts"
             "&MinSegments=1&BreakOnNonKeyFrames=true&TranscodingMaxAudioChannels=2")
    status, playlist = call("GET", f"/Videos/{item['Id']}/main.m3u8{query}", raw=True)
    segments = [l for l in playlist.decode().splitlines() if l and not l.startswith("#")] if status == 200 else []
    if not check(len(segments) > segment, f"[{session}] playlist has segments (status {status})"):
        return
    status, body = call("GET", f"/Videos/{item['Id']}/{segments[segment]}", raw=True, timeout=90)
    check(status == 200 and len(body) > 1000, f"[{session}] segment {segment} served (status {status})")


def transcodes(new, worker):
    return [args for args in new[worker] if "-hls_segment_filename" in args]


def main():
    items = setup()
    h264, av1 = items["H264 Movie"], items["AV1 Movie"]

    print("Plugin status", flush=True)
    status, plugin = call("GET", "/GrpcFfmpeg/Status")
    check(status == 200 and plugin.get("Active"), "plugin is active (Jellyfin runs ffmpeg through the client)")
    check(plugin.get("HardwareClassesActive"),
          f"hardware classes are active ({plugin.get('HardwareClassesUnavailable') or 'registered'})")
    check(not plugin.get("RestartRequired"), "no restart required")
    check("intel=" in (plugin.get("ClassAddresses") or "") and "nvidia=" in (plugin.get("ClassAddresses") or ""),
          f"CLASS_ADDRESSES written ({plugin.get('ClassAddresses')})")

    print("Encoder detection", flush=True)

    def detected():
        status, plugin = call("GET", "/GrpcFfmpeg/Status")
        results = {get(c, "Name"): get(c, "Check") for c in (plugin.get("ClassChecks") or [])} if status == 200 else {}
        status, config = call("GET", f"/Plugins/{PLUGIN_ID}/Configuration")
        saved = status == 200 and all(config[key]["HardwareDecodingCodecs"] == EXPECTED_CODECS[name]
                                      for name, key in (("intel", "IntelClass"), ("nvidia", "NvidiaClass")))
        done = all(results.get(name) and get(results[name], "DecodingTested") for name in EXPECTED_ENCODERS)
        return (results, config) if done and saved else None

    results, config = wait_for("the plugin's encoder and decoder detection (30 s after startup)", detected, timeout=300)
    for name, expected in EXPECTED_ENCODERS.items():
        found = get(results[name], "Encoders")
        check(found == expected, f"{name}: detected encoders {found}")
        found = get(results[name], "Decoders")
        check(found == EXPECTED_DECODERS[name], f"{name}: detected decoders {found}")
    check(config["IntelClass"]["AllowAv1Encoding"] is False and config["NvidiaClass"]["AllowAv1Encoding"] is True,
          "saved AV1 encoding corrected: off for intel, on for nvidia")
    for name, key in (("intel", "IntelClass"), ("nvidia", "NvidiaClass")):
        saved = config[key]
        check(saved["HardwareDecodingCodecs"] == EXPECTED_CODECS[name]
              and saved["EnableDecodingColorDepth10Hevc"] is True and saved["EnableDecodingColorDepth10Vp9"] is True,
              f"{name}: saved decoding corrected ({saved['HardwareDecodingCodecs']}, 10-bit HEVC/VP9 on)")

    print("Startup and library scan", flush=True)
    startup = counts()
    check(startup["default"] >= 5, f"startup checks ran on the default worker ({startup['default']} commands)")
    other = {w: [a for a in commands(w) if not is_gpu_check(a)] for w in ("intel", "nvidia")}
    check(not other["intel"] and not other["nvidia"],
          f"the class workers only got the GPU checks ({ {w: len(a) for w, a in other.items()} } other commands)")
    check(not any(has_arg(a, "-init_hw_device") for a in commands("default")), "no hardware command on the default worker")

    print("Auto policy", flush=True)
    before = counts()
    play(av1, "it-av1")
    new = new_commands(before)
    av1_jobs = transcodes(new, "nvidia")
    check(len(av1_jobs) == 1 and not transcodes(new, "intel") and not transcodes(new, "default"),
          "10-bit AV1 session transcodes on nvidia only (the only class decoding AV1)")
    check(bool(av1_jobs) and has_arg(av1_jobs[0], "cuda=") and "-hwaccel" in av1_jobs[0]
          and av1_jobs[0][av1_jobs[0].index("-hwaccel") + 1] == "cuda", "  with CUDA device and CUDA decoding")

    before = counts()
    play(h264, "it-h264")
    new = new_commands(before)
    h264_jobs = transcodes(new, "intel")
    check(len(h264_jobs) == 1 and not transcodes(new, "nvidia") and not transcodes(new, "default"),
          "next session goes to the less loaded class: intel")
    check(bool(h264_jobs) and has_arg(h264_jobs[0], "qsv=") and "h264_qsv" in h264_jobs[0], "  with QSV device and h264_qsv")

    before = counts()
    play(h264, "it-av1-out", codecs="av1,h264")
    new = new_commands(before)
    av1_out = transcodes(new, "nvidia")
    check(len(av1_out) == 1 and not transcodes(new, "intel"), "equal load: the class that encodes the client's AV1 wins (nvidia)")
    check(bool(av1_out) and "av1_nvenc" in av1_out[0], "  encoding with av1_nvenc")

    print("Seek", flush=True)
    before = counts()
    # Far enough ahead (>24 s) that Jellyfin restarts ffmpeg instead of waiting for it
    play(h264, "it-h264", segment=18)
    new = new_commands(before)
    seek = transcodes(new, "intel")
    check(len(seek) == 1 and not transcodes(new, "nvidia") and not transcodes(new, "default"), "seek keeps the session on intel")
    check(bool(seek) and "-ss" in seek[0] and seek[0][seek[0].index("-start_number") + 1] == "18", "  restarted at segment 18")

    print("Isolation", flush=True)
    check(all(has_arg(a, "qsv=") for a in commands("intel") if "-hls_segment_filename" in a), "intel worker received only QSV transcodes")
    check(all(has_arg(a, "cuda=") for a in commands("nvidia") if "-hls_segment_filename" in a), "nvidia worker received only CUDA transcodes")
    check(not any(has_arg(a, "-init_hw_device") for a in commands("default")), "default worker received no hardware command")

    print("Jellyfin's settings and the plugin's diagnostics", flush=True)
    status, encoding = call("GET", "/System/Configuration/encoding")
    check(status == 200 and encoding.get("HardwareAccelerationType") in ("none", 0),
          f"Jellyfin's saved hardware acceleration is unchanged ({encoding.get('HardwareAccelerationType') if status == 200 else status})")
    status, plugin = call("GET", "/GrpcFfmpeg/Status")
    classes = plugin.get("HardwareClasses") or {}
    print("  status: " + json.dumps(classes), flush=True)
    check(not classes.get("Warnings"), f"no hardware class warnings ({classes.get('Warnings')})")
    check(classes.get("ClassResolutions", 0) > 0, "Jellyfin read the class settings during streaming requests")
    routed = {c["Name"]: c["RoutedCommands"] for c in classes.get("Classes", [])}
    check(routed.get("intel", 0) > 0 and routed.get("nvidia", 0) > 0, f"the client routed commands to both classes ({routed})")
    check(not plugin.get("Fallback"), f"no command fell back to the local ffmpeg ({plugin.get('Fallback')})")

    if failures:
        print(f"\n{len(failures)} check(s) failed:\n- " + "\n- ".join(failures))
        sys.exit(1)
    print("\nAll checks passed")


if __name__ == "__main__":
    main()
