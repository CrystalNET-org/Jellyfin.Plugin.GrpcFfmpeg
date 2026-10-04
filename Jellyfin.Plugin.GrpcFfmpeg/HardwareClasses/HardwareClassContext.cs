using Jellyfin.Plugin.GrpcFfmpeg.Configuration;

namespace Jellyfin.Plugin.GrpcFfmpeg.HardwareClasses
{
    /// <summary>
    /// The streaming request being handled, and through it the hardware class of its
    /// playback session.
    /// </summary>
    /// <remarks>
    /// Flows with the request's async calls, including the ffmpeg process Jellyfin
    /// starts for it and the tasks that follow its output. Unset outside of streaming
    /// requests (startup, scheduled tasks, library scans), so those use Jellyfin's own
    /// transcoding settings.
    /// </remarks>
    internal static class HardwareClassContext
    {
        private static readonly AsyncLocal<StreamRequest?> _request = new();

        /// <summary>
        /// Gets or sets a value indicating whether hardware classes were enabled at startup.
        /// </summary>
        public static bool Active { get; set; }

        public static StreamRequest? Request
        {
            get => _request.Value;
            set => _request.Value = value;
        }

        /// <summary>
        /// Gets the class of the current request's session, choosing it on first use.
        /// </summary>
        public static HardwareClassSettings? Current => _request.Value?.Resolve();
    }
}
