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
    internal sealed record ClassProbeResult(
        string Class,
        string Address,
        DateTime CheckedAt,
        bool Reachable,
        string? Version,
        IReadOnlyList<string> Encoders,
        string? Error)
    {
        /// <summary>
        /// Gets a value indicating whether the GPU works: it encoded H.264, which every
        /// supported GPU does. Only then do the other results say what the GPU can do.
        /// </summary>
        public bool GpuUsable => Encoders.Contains("h264");
    }

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

        private static readonly ConcurrentDictionary<string, ClassProbeResult> _latest = new(StringComparer.Ordinal);

        /// <summary>
        /// Runs a client command: (binary name, arguments) → result.
        /// </summary>
        public delegate Task<CommandResult> ClientRunner(string name, IReadOnlyList<string> arguments);

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

            // EncodingHelper.GetQsvDeviceArgs on Linux: QSV derived from VAAPI on the render node
            var device = string.IsNullOrWhiteSpace(hardwareClass.Device)
                ? "va:,vendor_id=0x8086,driver=iHD"
                : "va:" + hardwareClass.Device.Trim() + ",driver=iHD";
            return new[] { "-hide_banner", "-v", "error", "-init_hw_device", "vaapi=" + device, "-init_hw_device", "qsv=qs@va", "-filter_hw_device", "qs" }
                .Concat(input)
                .Concat(new[] { "-vf", "format=nv12,hwupload=extra_hw_frames=16,format=qsv", "-c:v", codec + "_qsv" })
                .Concat(output)
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

            return Remember(new ClassProbeResult(hardwareClass.Name, address, DateTime.UtcNow, true, versionLine, encoders, error));
        }

        /// <summary>
        /// Sets the class's HEVC and AV1 encoding from the result, if it is about the class's
        /// current workers and their GPU works.
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
