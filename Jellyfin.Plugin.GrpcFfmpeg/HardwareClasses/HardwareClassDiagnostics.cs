using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.GrpcFfmpeg.HardwareClasses
{
    /// <summary>
    /// Counters that reveal the ways hardware classes can stop working without any error,
    /// for the status page and the integration pipeline.
    /// </summary>
    /// <remarks>
    /// - Jellyfin no longer reads its transcoding settings through the configuration manager
    ///   during streaming requests: HLS requests arrive, but no class is ever chosen.
    /// - The client ignores CLASS_ADDRESSES (too old): its log lines carry no class.
    /// - Commands of a class's sessions do not reach that class's workers (e.g. Jellyfin
    ///   changed its hardware arguments): sessions transcode, but no command is routed there.
    /// - Errors while choosing or applying a class, which are caught so playback continues
    ///   with Jellyfin's own settings.
    /// </remarks>
    internal static class HardwareClassDiagnostics
    {
        /// <summary>HLS requests without any class choice before warning.</summary>
        public const int MinHlsRequests = 3;

        /// <summary>Transcoding sessions of a class without any routed command before warning.</summary>
        public const int MinTranscodingSessions = 2;

        private static readonly ConcurrentDictionary<string, long> _routed = new(StringComparer.Ordinal);
        private static long _hlsRequests;
        private static long _resolutions;
        private static long _unroutedRuns;
        private static long _errors;
        private static string? _lastError;

        public static ILogger? Logger { get; set; }

        public static long HlsRequests => Interlocked.Read(ref _hlsRequests);

        /// <summary>Gets how often Jellyfin read its transcoding settings during a streaming request.</summary>
        public static long Resolutions => Interlocked.Read(ref _resolutions);

        public static long UnroutedRuns => Interlocked.Read(ref _unroutedRuns);

        public static long Errors => Interlocked.Read(ref _errors);

        public static void StreamingRequest(string path)
        {
            if (path.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
                || path.Contains("/hls", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _hlsRequests);
            }
        }

        public static void Resolved() => Interlocked.Increment(ref _resolutions);

        /// <summary>
        /// Records a command the client ran: its class ("default" for unclassified), or null
        /// for a client line without any class (a client that ignores CLASS_ADDRESSES).
        /// </summary>
        public static void ClientRun(string? className)
        {
            if (className is null)
            {
                Interlocked.Increment(ref _unroutedRuns);
            }
            else
            {
                _routed.AddOrUpdate(className, 1, (_, count) => count + 1);
            }
        }

        public static long Routed(string className) => _routed.TryGetValue(className, out var count) ? count : 0;

        public static void Error(string where, Exception ex)
        {
            var count = Interlocked.Increment(ref _errors);
            _lastError = $"{where}: {ex.GetType().Name}: {ex.Message}";
            // The first few in full, then only now and then
            if (count <= 5 || count % 100 == 0)
            {
                Logger?.LogError(ex, "gRPC-ffmpeg: hardware classes: {Where} failed ({Count} times so far); using Jellyfin's settings for this request", where, count);
            }
        }

        /// <summary>
        /// Gets the warnings for the current state.
        /// </summary>
        /// <param name="classAddressesWritten">Whether CLASS_ADDRESSES is in the client config.</param>
        /// <param name="classes">Enabled class names.</param>
        /// <param name="transcodingSessions">Gets the number of a class's sessions with a running transcode.</param>
        public static List<string> Warnings(bool classAddressesWritten, IEnumerable<string> classes, Func<string, int> transcodingSessions)
        {
            var warnings = new List<string>();
            if (HlsRequests >= MinHlsRequests && Resolutions == 0)
            {
                warnings.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "Jellyfin read no transcoding settings during {0} HLS requests, so hardware classes have no effect with this Jellyfin version.",
                    HlsRequests));
            }

            if (classAddressesWritten && UnroutedRuns > 0)
            {
                warnings.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "The grpc-ffmpeg client ignored CLASS_ADDRESSES for {0} commands (client too old?): they all went to the default worker.",
                    UnroutedRuns));
            }

            foreach (var name in classes)
            {
                var sessions = transcodingSessions(name);
                if (sessions >= MinTranscodingSessions && Routed(name) == 0)
                {
                    warnings.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} sessions of class {1} are transcoding, but no command reached its workers: Jellyfin's hardware arguments may have changed.",
                        sessions,
                        name));
                }
            }

            if (Errors > 0)
            {
                warnings.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "Choosing or applying a hardware class failed {0} times (last: {1}); those requests used Jellyfin's settings.",
                    Errors,
                    _lastError));
            }

            return warnings;
        }

        /// <summary>
        /// Gets the counters per class, for the status page.
        /// </summary>
        public static Dictionary<string, long> RoutedCommands() => new(_routed);

        /// <summary>
        /// Resets all counters (tests).
        /// </summary>
        public static void Reset()
        {
            _routed.Clear();
            Interlocked.Exchange(ref _hlsRequests, 0);
            Interlocked.Exchange(ref _resolutions, 0);
            Interlocked.Exchange(ref _unroutedRuns, 0);
            Interlocked.Exchange(ref _errors, 0);
            _lastError = null;
        }
    }
}
