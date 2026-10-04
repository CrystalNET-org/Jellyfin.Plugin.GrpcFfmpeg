using System.Collections.Concurrent;
using Jellyfin.Plugin.GrpcFfmpeg.Configuration;

namespace Jellyfin.Plugin.GrpcFfmpeg.HardwareClasses
{
    /// <summary>
    /// Picks the hardware class of a playback session and keeps it for the whole session,
    /// so seeks and later segment requests run on the same kind of GPU.
    /// </summary>
    internal sealed class SessionClassSelector
    {
        /// <summary>Sessions not seen for this long are forgotten.</summary>
        public static readonly TimeSpan Expiry = TimeSpan.FromHours(6);

        private readonly ConcurrentDictionary<string, Assignment> _sessions = new(StringComparer.Ordinal);
        private readonly Func<PluginConfiguration?> _configuration;
        private readonly Func<DateTime> _now;
        private readonly object _turnLock = new();
        private int _turn;
        private DateTime _nextPurge;

        public SessionClassSelector(Func<PluginConfiguration?> configuration, Func<DateTime>? now = null)
        {
            _configuration = configuration;
            _now = now ?? (() => DateTime.UtcNow);
        }

        /// <summary>
        /// Gets the number of remembered sessions.
        /// </summary>
        public int Count => _sessions.Count;

        /// <summary>
        /// Gets the class of the session, choosing one for a new session.
        /// </summary>
        /// <param name="playSessionId">Jellyfin's PlaySessionId.</param>
        /// <param name="isNew">Whether the class was chosen just now.</param>
        /// <returns>The class, or null to use Jellyfin's own settings.</returns>
        public HardwareClassSettings? Select(string playSessionId, out bool isNew)
        {
            isNew = false;
            var config = _configuration();
            if (config is null || !config.EnableHardwareClasses)
            {
                return null;
            }

            var now = _now();
            PurgeExpired(now);

            var created = false;
            var assignment = _sessions.AddOrUpdate(
                playSessionId,
                _ =>
                {
                    created = true;
                    return new Assignment(Choose(config), now);
                },
                (_, existing) => existing with { LastSeen = now });
            isNew = created;

            // Looked up on every request, so changed addresses and settings apply right
            // away; a class disabled meanwhile falls back to Jellyfin's settings
            return config.HardwareClasses().FirstOrDefault(c => c.Name == assignment.ClassName && c.Address is not null);
        }

        private string? Choose(PluginConfiguration config)
        {
            var usable = config.HardwareClasses().Where(c => c.Address is not null).ToList();
            var wanted = config.DefaultHardwareClass?.Trim().ToLowerInvariant() ?? string.Empty;
            if (wanted == HardwareClassSettings.Alternate)
            {
                if (usable.Count == 0)
                {
                    return null;
                }

                lock (_turnLock)
                {
                    return usable[_turn++ % usable.Count].Name;
                }
            }

            return usable.FirstOrDefault(c => c.Name == wanted)?.Name;
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

        private sealed record Assignment(string? ClassName, DateTime LastSeen);
    }
}
