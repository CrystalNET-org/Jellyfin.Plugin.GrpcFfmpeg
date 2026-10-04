using System.Collections.Concurrent;
using Jellyfin.Plugin.GrpcFfmpeg.Configuration;

namespace Jellyfin.Plugin.GrpcFfmpeg.HardwareClasses
{
    /// <summary>
    /// What a hardware class's workers answered when checked.
    /// </summary>
    /// <param name="Class">Class name.</param>
    /// <param name="Address">Address of the workers that were checked.</param>
    /// <param name="CheckedAt">When (UTC).</param>
    /// <param name="Reachable">Whether the workers answered.</param>
    /// <param name="Version">First line of their ffmpeg -version.</param>
    /// <param name="Encoders">Codecs ("h264", "hevc", "av1") their GPU encoded.</param>
    /// <param name="Error">Why the workers or their GPU could not be used, or null.</param>
    /// <param name="Decoders">Test clips (<see cref="ClassProbe.DecodeTests"/> ids) their GPU decoded.</param>
    /// <param name="DecodeTested">Test clips the workers could make, i.e. whose result means something.</param>
    internal sealed record ClassProbeResult(
        string Class,
        string Address,
        DateTime CheckedAt,
        bool Reachable,
        string? Version,
        IReadOnlyList<string> Encoders,
        string? Error,
        IReadOnlyList<string>? Decoders = null,
        IReadOnlyList<string>? DecodeTested = null)
    {
        /// <summary>
        /// Gets a value indicating whether the GPU works: it encoded H.264, which every
        /// supported GPU does. Only then do the other results say what the GPU can do.
        /// </summary>
        public bool GpuUsable => Encoders.Contains("h264");

        /// <summary>
        /// Gets a value indicating whether the decoding results say what the GPU can do: it
        /// decoded H.264, which every supported GPU does. Otherwise the test itself failed.
        /// </summary>
        public bool DecodingTested => Decoders?.Contains("h264") == true;
    }

    /// <summary>
    /// A decoding test: a short clip the workers encode in software, then decode on the GPU.
    /// </summary>
    /// <param name="Id">Result id: the codec as Jellyfin names it, "hevc10"/"vp910" for 10-bit.</param>
    /// <param name="Name">Display name.</param>
    /// <param name="Encoder">Software encoder and its options for the clip.</param>
    /// <param name="Nvidia">NVDEC decoder.</param>
    /// <param name="Qsv">QSV decoder.</param>
    internal sealed record DecodeTest(string Id, string Name, string[] Encoder, string Nvidia, string Qsv);

    /// <summary>
    /// Finds out which codecs a hardware class's GPUs can encode, by having its workers
    /// encode a few frames with each, set up like Jellyfin's own commands for the class.
    /// The ffmpeg build lists every encoder whether the GPU supports it or not (e.g.
    /// av1_nvenc on a GPU without AV1), so only a real encode tells.
    /// </summary>
    internal static class ClassProbe
    {
        /// <summary>Codecs checked, in order; H.264 first, as it shows whether the GPU works at all.</summary>
        public static readonly string[] Codecs = { "h264", "hevc", "av1" };

        /// <summary>
        /// Decoding tests; H.264 first, as it shows whether the test works at all. VC-1 has no
        /// software encoder, so it cannot be tested. AV1 is tested with a 10-bit clip, the
        /// common case, which every GPU with AV1 decoding supports.
        /// </summary>
        public static readonly DecodeTest[] DecodeTests =
        {
            new("h264", "H.264", new[] { "-c:v", "libx264", "-pix_fmt", "yuv420p" }, "h264_cuvid", "h264_qsv"),
            new("hevc", "HEVC", new[] { "-c:v", "libx265", "-pix_fmt", "yuv420p", "-x265-params", "log-level=none" }, "hevc_cuvid", "hevc_qsv"),
            new("hevc10", "HEVC 10-bit", new[] { "-c:v", "libx265", "-pix_fmt", "yuv420p10le", "-x265-params", "log-level=none" }, "hevc_cuvid", "hevc_qsv"),
            new("mpeg2video", "MPEG-2", new[] { "-c:v", "mpeg2video", "-pix_fmt", "yuv420p" }, "mpeg2_cuvid", "mpeg2_qsv"),
            new("vp8", "VP8", new[] { "-c:v", "libvpx", "-pix_fmt", "yuv420p", "-deadline", "realtime", "-cpu-used", "8" }, "vp8_cuvid", "vp8_qsv"),
            new("vp9", "VP9", new[] { "-c:v", "libvpx-vp9", "-pix_fmt", "yuv420p", "-deadline", "realtime", "-cpu-used", "8" }, "vp9_cuvid", "vp9_qsv"),
            new("vp910", "VP9 10-bit", new[] { "-c:v", "libvpx-vp9", "-pix_fmt", "yuv420p10le", "-profile:v", "2", "-deadline", "realtime", "-cpu-used", "8" }, "vp9_cuvid", "vp9_qsv"),
            new("av1", "AV1", new[] { "-c:v", "libsvtav1", "-pix_fmt", "yuv420p10le", "-preset", "12" }, "av1_cuvid", "av1_qsv"),
        };

        /// <summary>Codecs Jellyfin can decode in hardware, in the order of its settings.</summary>
        public static readonly string[] DecodingCodecs = { "h264", "hevc", "mpeg2video", "vc1", "vp8", "vp9", "av1" };

        private static readonly ConcurrentDictionary<string, ClassProbeResult> _latest = new(StringComparer.Ordinal);

        /// <summary>
        /// Runs a client command: (binary name, arguments, data for its stdin) → result.
        /// </summary>
        public delegate Task<CommandResult> ClientRunner(string name, IReadOnlyList<string> arguments, byte[]? stdin = null);

        /// <summary>
        /// Gets the latest result for the class, or null if it was not checked yet.
        /// </summary>
        public static ClassProbeResult? Latest(string className) =>
            _latest.TryGetValue(className, out var result) ? result : null;

        /// <summary>
        /// Gets the arguments of a short test encode with the class's GPU.
        /// </summary>
        public static string[] EncodeTestArguments(HardwareClassSettings hardwareClass, string codec)
        {
            var input = new[] { "-f", "lavfi", "-i", "color=c=black:s=640x360:r=25:d=1" };
            var output = new[] { "-frames:v", "3", "-f", "null", "-" };
            if (hardwareClass.AccelerationType == MediaBrowser.Model.Entities.HardwareAccelerationType.nvenc)
            {
                // EncodingHelper.GetCudaDeviceArgs; NVENC takes the frames from system memory
                return new[] { "-hide_banner", "-v", "error", "-init_hw_device", "cuda=cu:0" }
                    .Concat(input)
                    .Concat(new[] { "-c:v", codec + "_nvenc" })
                    .Concat(output)
                    .ToArray();
            }

            return new[] { "-hide_banner", "-v", "error", "-init_hw_device", "vaapi=" + QsvDevice(hardwareClass), "-init_hw_device", "qsv=qs@va", "-filter_hw_device", "qs" }
                .Concat(input)
                .Concat(new[] { "-vf", "format=nv12,hwupload=extra_hw_frames=16,format=qsv", "-c:v", codec + "_qsv" })
                .Concat(output)
                .ToArray();
        }

        /// <summary>
        /// Gets the arguments that make a test clip in software, written to stdout.
        /// </summary>
        public static string[] ClipArguments(DecodeTest test) =>
            new[] { "-hide_banner", "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=0.4" }
                .Concat(test.Encoder)
                .Concat(new[] { "-frames:v", "10", "-f", "matroska", "pipe:1" })
                .ToArray();

        /// <summary>
        /// Gets the arguments that decode a test clip from stdin with the GPU's decoder, which
        /// (unlike -hwaccel) fails rather than falling back to software when the GPU cannot.
        /// </summary>
        public static string[] DecodeTestArguments(HardwareClassSettings hardwareClass, DecodeTest test)
        {
            var decode = new[] { "-f", "matroska", "-i", "pipe:0", "-frames:v", "3", "-f", "null", "-" };
            if (hardwareClass.AccelerationType == MediaBrowser.Model.Entities.HardwareAccelerationType.nvenc)
            {
                // The CUDA device also gets the command its GPU on workers with CUDA_DEVICES
                return new[] { "-hide_banner", "-v", "error", "-init_hw_device", "cuda=cu:0", "-c:v", test.Nvidia }.Concat(decode).ToArray();
            }

            return new[] { "-hide_banner", "-v", "error", "-init_hw_device", "vaapi=" + QsvDevice(hardwareClass), "-init_hw_device", "qsv=qs@va", "-hwaccel", "qsv", "-hwaccel_device", "qs", "-c:v", test.Qsv }
                .Concat(decode)
                .ToArray();
        }

        /// <summary>
        /// Checks the class's workers. The runner must send the commands to them (and only
        /// them, without the local fallback).
        /// </summary>
        public static async Task<ClassProbeResult> RunAsync(HardwareClassSettings hardwareClass, ClientRunner run)
        {
            var address = hardwareClass.Address ?? string.Empty;
            var version = await run("ffmpeg", new[] { "-hide_banner", "-version" }).ConfigureAwait(false);
            if (version.ExitCode != 0)
            {
                return Remember(new ClassProbeResult(hardwareClass.Name, address, DateTime.UtcNow, false, null, Array.Empty<string>(), LastLine(version.Error)));
            }

            var versionLine = version.Output.Split('\n')[0].Trim();
            var encoders = new List<string>();
            string? error = null;
            foreach (var codec in Codecs)
            {
                var result = await run("ffmpeg", EncodeTestArguments(hardwareClass, codec)).ConfigureAwait(false);
                if (result.ExitCode == 0)
                {
                    encoders.Add(codec);
                }
                else if (codec == "h264")
                {
                    // The GPU (or its driver or device access) does not work; the rest would fail the same way
                    error = "The GPU could not encode H.264: " + LastLine(result.Error);
                    break;
                }
            }

            var decoders = new List<string>();
            var tested = new List<string>();
            if (error is null)
            {
                foreach (var test in DecodeTests)
                {
                    var clip = await run("ffmpeg", ClipArguments(test)).ConfigureAwait(false);
                    if (clip.ExitCode != 0 || clip.Data is not { Length: > 0 })
                    {
                        // The workers' ffmpeg lacks the software encoder: unknown, not unsupported
                        continue;
                    }

                    tested.Add(test.Id);
                    var decode = await run("ffmpeg", DecodeTestArguments(hardwareClass, test), clip.Data).ConfigureAwait(false);
                    if (decode.ExitCode == 0)
                    {
                        decoders.Add(test.Id);
                    }
                }
            }

            return Remember(new ClassProbeResult(hardwareClass.Name, address, DateTime.UtcNow, true, versionLine, encoders, error, decoders, tested));
        }

        // EncodingHelper.GetQsvDeviceArgs on Linux: QSV derived from VAAPI on the render node
        private static string QsvDevice(HardwareClassSettings hardwareClass) =>
            string.IsNullOrWhiteSpace(hardwareClass.Device)
                ? "va:,vendor_id=0x8086,driver=iHD"
                : "va:" + hardwareClass.Device.Trim() + ",driver=iHD";

        /// <summary>
        /// Sets the class's HEVC and AV1 encoding, and if the decoding test worked, its
        /// hardware decoding codecs and 10-bit decoding, from the result, if it is about the
        /// class's current workers and their GPU works. Codecs that could not be tested
        /// (VC-1, or a software encoder the workers lack) keep their setting.
        /// </summary>
        /// <returns>Whether a setting changed.</returns>
        public static bool Apply(PluginConfiguration config, ClassProbeResult result)
        {
            var hardwareClass = config.HardwareClasses().FirstOrDefault(c => c.Name == result.Class);
            if (hardwareClass is null || !result.GpuUsable || hardwareClass.Address != result.Address)
            {
                return false;
            }

            var hevc = result.Encoders.Contains("hevc");
            var av1 = result.Encoders.Contains("av1");
            var changed = hardwareClass.AllowHevcEncoding != hevc || hardwareClass.AllowAv1Encoding != av1;
            hardwareClass.AllowHevcEncoding = hevc;
            hardwareClass.AllowAv1Encoding = av1;
            if (!result.DecodingTested)
            {
                return changed;
            }

            bool? Decodes(string id) => result.DecodeTested!.Contains(id) ? result.Decoders!.Contains(id) : null;
            var current = hardwareClass.HardwareDecodingCodecs ?? Array.Empty<string>();
            var codecs = DecodingCodecs
                .Where(codec => Decodes(codec) ?? current.Contains(codec, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            var hevc10 = Decodes("hevc10") ?? hardwareClass.EnableDecodingColorDepth10Hevc;
            var vp910 = Decodes("vp910") ?? hardwareClass.EnableDecodingColorDepth10Vp9;
            changed |= !codecs.SequenceEqual(current, StringComparer.OrdinalIgnoreCase)
                || hevc10 != hardwareClass.EnableDecodingColorDepth10Hevc
                || vp910 != hardwareClass.EnableDecodingColorDepth10Vp9;
            hardwareClass.HardwareDecodingCodecs = codecs;
            hardwareClass.EnableDecodingColorDepth10Hevc = hevc10;
            hardwareClass.EnableDecodingColorDepth10Vp9 = vp910;
            return changed;
        }

        /// <summary>
        /// Forgets all results (tests).
        /// </summary>
        public static void Reset() => _latest.Clear();

        private static ClassProbeResult Remember(ClassProbeResult result)
        {
            if (result.Address.Length > 0)
            {
                _latest[result.Class] = result;
            }

            return result;
        }

        private static string LastLine(string text)
        {
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return lines.Length > 0 ? lines[^1] : "no error output";
        }
    }
}
