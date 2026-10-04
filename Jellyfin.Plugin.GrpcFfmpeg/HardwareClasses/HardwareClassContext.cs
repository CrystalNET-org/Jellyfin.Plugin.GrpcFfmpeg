using Jellyfin.Plugin.GrpcFfmpeg.Configuration;

namespace Jellyfin.Plugin.GrpcFfmpeg.HardwareClasses
{
    /// <summary>
    /// The hardware class of the playback session the current request belongs to.
    /// </summary>
    /// <remarks>
    /// Flows with the request's async calls, including the ffmpeg process Jellyfin
    /// starts for it and the tasks that follow its output. Unset outside of streaming
    /// requests (startup, scheduled tasks, library scans), so those use Jellyfin's own
    /// transcoding settings.
    /// </remarks>
    internal static class HardwareClassContext
    {
        private static readonly AsyncLocal<HardwareClassSettings?> _current = new();

        /// <summary>
        /// Gets or sets a value indicating whether hardware classes were enabled at startup.
        /// </summary>
        public static bool Active { get; set; }

        public static HardwareClassSettings? Current
        {
            get => _current.Value;
            set => _current.Value = value;
        }
    }
}
