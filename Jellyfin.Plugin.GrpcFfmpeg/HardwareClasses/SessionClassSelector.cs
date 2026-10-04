using System.Collections.Concurrent;
using Jellyfin.Plugin.GrpcFfmpeg.Configuration;

namespace Jellyfin.Plugin.GrpcFfmpeg.HardwareClasses
{
    /// <summary>
    /// The video stream of the media being played, as far as class selection needs it.
    /// </summary>
    /// <param name="Codec">Codec as Jellyfin names it ("h264", "hevc", "av1", …).</param>
    /// <param name="BitDepth">Bit depth, if known.</param>
    internal sealed record SourceVideo(string Codec, int? BitDepth);

    /// <summary>
    /// Picks the hardware class of a playback session and keeps it for the whole session,
    /// so seeks and later segment requests run on the same kind of GPU.
    /// </summary>
    /// <remarks>
    /// The "auto" policy considers, in this order: classes whose workers are not known to
    /// be unreachable; classes that can decode the source video in hardware (if any can);
    /// the lowest load (running transcodes per weight); classes that can encode the codec
    /// the client prefers; and finally takes turns.
    /// </remarks>
    internal sealed class SessionClassSelector
    {
        /// <summary>Sessions not seen for this long are forgotten.</summary>
        public static readonly TimeSpan Expiry = TimeSpan.FromHours(6);

        /// <summary>A session counts towards its class's load for this long after it was assigned, before its transcode shows up.</summary>
        public static readonly TimeSpan StartingWindow = TimeSpan.FromSeconds(30);

        private readonly ConcurrentDictionary<string, Assignment> _sessions = new(StringComparer.Ordinal);
        private readonly Func<PluginConfiguration?> _configuration;
        private readonly Func<StreamRequest, SourceVideo?> _sourceVideo;
        private readonly Func<string, bool> _isTranscoding;
        private readonly Func<HardwareClassSettings, bool> _isUnreachable;
        private readonly Func<DateTime> _now;
        private readonly object _lock = new();
        private int _turn;
        private DateTime _nextPurge;

        public SessionClassSelector(
            Func<PluginConfiguration?> configuration,
            Func<StreamRequest, SourceVideo?>? sourceVideo = null,
            Func<string, bool>? isTranscoding = null,
            Func<HardwareClassSettings, bool>? isUnreachable = null,
            Func<DateTime>? now = null)
        {
            _configuration = configuration;
            _sourceVideo = sourceVideo ?? (_ => null);
            _isTranscoding = isTranscoding ?? (_ => false);
            _isUnreachable = isUnreachable ?? (_ => false);
            _now = now ?? (() => DateTime.UtcNow);
        }

        /// <summary>
        /// Raised when a session gets a class: (PlaySessionId, class or null, reason).
        /// </summary>
        public event Action<string, HardwareClassSettings?, string>? Assigned;

        /// <summary>
        /// Gets the number of remembered sessions.
        /// </summary>
        public int Count => _sessions.Count;

        /// <summary>
        /// Gets the class of the request's session, choosing one for a new session.
        /// </summary>
        /// <returns>The class, or null to use Jellyfin's own settings.</returns>
        public HardwareClassSettings? Select(StreamRequest request)
        {
            var config = _configuration();
            if (config is null || !config.EnableHardwareClasses)
            {
                return null;
            }

            var now = _now();
            PurgeExpired(now);

            HardwareClassSettings? chosen;
            string reason;
            lock (_lock)
            {
                if (_sessions.TryGetValue(request.PlaySessionId, out var existing))
                {
                    var current = Usable(config).FirstOrDefault(c => c.Name == existing.ClassName);
                    // Keep the class unless its workers went down and nothing is running
                    // for the session, i.e. it is starting over (or its transcode failed)
                    if (existing.ClassName is null
                        || (current is not null && !(_isUnreachable(current) && !_isTranscoding(request.PlaySessionId))))
                    {
                        _sessions[request.PlaySessionId] = existing with { LastSeen = now };
                        return current;
                    }
                }

                (chosen, reason) = Choose(config, request, now);
                _sessions[request.PlaySessionId] = new Assignment(chosen?.Name, now, now);
            }

            Assigned?.Invoke(request.PlaySessionId, chosen, reason);
            return chosen;
        }

        /// <summary>
        /// Gets the number of the class's sessions that are transcoding or just starting.
        /// </summary>
        public int Load(string className)
        {
            var now = _now();
            return _sessions.Count(pair => pair.Value.ClassName == className
                && (now - pair.Value.AssignedAt < StartingWindow || _isTranscoding(pair.Key)));
        }

        /// <summary>
        /// Gets whether the class can decode the video in hardware with its settings.
        /// </summary>
        public static bool CanDecode(HardwareClassSettings hardwareClass, SourceVideo video)
        {
            var codec = video.Codec.ToLowerInvariant();
            if (!hardwareClass.HardwareDecodingCodecs.Contains(codec, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            var depth = video.BitDepth ?? 8;
            return codec switch
            {
                "hevc" => depth <= 8 || (depth == 10 && hardwareClass.EnableDecodingColorDepth10Hevc),
                "vp9" => depth <= 8 || (depth == 10 && hardwareClass.EnableDecodingColorDepth10Vp9),
                "av1" => depth <= 10,
                // e.g. 10-bit H.264 is decoded in software by every class
                _ => depth <= 8,
            };
        }

        /// <summary>
        /// Gets the position of the first requested codec the class can encode (lower is better).
        /// </summary>
        public static int EncodeRank(HardwareClassSettings hardwareClass, IReadOnlyList<string> requested)
        {
            for (var i = 0; i < requested.Count; i++)
            {
                var codec = requested[i].ToLowerInvariant();
                if (codec == "h264"
                    || (codec is "hevc" or "h265" && hardwareClass.AllowHevcEncoding)
                    || (codec == "av1" && hardwareClass.AllowAv1Encoding))
                {
                    return i;
                }
            }

            return requested.Count;
        }

        private static List<HardwareClassSettings> Usable(PluginConfiguration config) =>
            config.HardwareClasses().Where(c => c.Address is not null).ToList();

        private (HardwareClassSettings? Class, string Reason) Choose(PluginConfiguration config, StreamRequest request, DateTime now)
        {
            var usable = Usable(config);
            var policy = config.DefaultHardwareClass?.Trim().ToLowerInvariant() ?? string.Empty;
            if (usable.Count == 0 || policy.Length == 0)
            {
                return (null, usable.Count == 0 ? "no class enabled" : "default policy");
            }

            if (policy == HardwareClassSettings.Alternate)
            {
                return (usable[_turn++ % usable.Count], "taking turns");
            }

            if (policy != HardwareClassSettings.Auto)
            {
                var fixedClass = usable.FirstOrDefault(c => c.Name == policy);
                return (fixedClass, fixedClass is null ? "default class not enabled" : "default class");
            }

            var reasons = new List<string>();
            var candidates = usable;

            var reachable = candidates.Where(c => !_isUnreachable(c)).ToList();
            if (reachable.Count > 0 && reachable.Count < candidates.Count)
            {
                reasons.Add("skipped unreachable " + string.Join("/", candidates.Except(reachable).Select(c => c.Name)));
                candidates = reachable;
            }

            var video = SafeSourceVideo(request);
            if (video is not null)
            {
                var decoding = candidates.Where(c => CanDecode(c, video)).ToList();
                if (decoding.Count > 0 && decoding.Count < candidates.Count)
                {
                    reasons.Add($"hardware decoding of {video.Codec}{(video.BitDepth > 8 ? $" {video.BitDepth}-bit" : string.Empty)}");
                    candidates = decoding;
                }
            }

            var loads = candidates.ToDictionary(c => c.Name, c => Load(c.Name) / (double)Math.Max(1, c.Weight));
            var lowest = loads.Values.Min();
            var leastLoaded = candidates.Where(c => loads[c.Name] <= lowest + 1e-9).ToList();
            if (leastLoaded.Count < candidates.Count)
            {
                reasons.Add("least loaded");
                candidates = leastLoaded;
            }

            if (request.RequestedVideoCodecs.Count > 0)
            {
                var best = candidates.Min(c => EncodeRank(c, request.RequestedVideoCodecs));
                var encoding = candidates.Where(c => EncodeRank(c, request.RequestedVideoCodecs) == best).ToList();
                if (encoding.Count < candidates.Count && best < request.RequestedVideoCodecs.Count)
                {
                    reasons.Add("encodes " + request.RequestedVideoCodecs[best]);
                    candidates = encoding;
                }
            }

            var chosen = candidates[_turn++ % candidates.Count];
            if (candidates.Count > 1)
            {
                reasons.Add("taking turns");
            }

            reasons.Add("load " + string.Join(", ", usable.Select(c => $"{c.Name} {Load(c.Name)}/{Math.Max(1, c.Weight)}")));
            return (chosen, string.Join("; ", reasons));
        }

        private SourceVideo? SafeSourceVideo(StreamRequest request)
        {
            try
            {
                return _sourceVideo(request);
            }
            catch (Exception)
            {
                // Selection works without it
                return null;
            }
        }

        private void PurgeExpired(DateTime now)
        {
            if (now < _nextPurge)
            {
                return;
            }

            _nextPurge = now + TimeSpan.FromMinutes(5);
            foreach (var (id, assignment) in _sessions)
            {
                if (now - assignment.LastSeen > Expiry)
                {
                    _sessions.TryRemove(id, out _);
                }
            }
        }

        private sealed record Assignment(string? ClassName, DateTime AssignedAt, DateTime LastSeen);
    }
}
