using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.GrpcFfmpeg.Configuration
{
    /// <summary>
    /// Plugin settings. They are written to the client's grpc-ffmpeg.conf.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Gets or sets a value indicating whether Jellyfin runs ffmpeg through the
        /// gRPC workers. Takes effect after a server restart.
        /// </summary>
        public bool Enabled { get; set; }

        public string GrpcHost { get; set; } = "ffmpeg-workers";

        public int GrpcPort { get; set; } = 50051;

        public string AuthToken { get; set; } = string.Empty;

        public bool UseSsl { get; set; }

        public string CertificatePath { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether commands run with the local ffmpeg
        /// when no worker is reachable. Jellyfin does not start without a working
        /// ffmpeg, so this is on by default.
        /// </summary>
        public bool EnableFallback { get; set; } = true;

        /// <summary>
        /// Gets or sets the directory of the local ffmpeg used as fallback. Empty means
        /// the ffmpeg Jellyfin would use without this plugin.
        /// </summary>
        public string FallbackDirectory { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the number of attempts while no worker is reachable or all are busy.
        /// </summary>
        public int Retries { get; set; } = 2;

        /// <summary>
        /// Gets or sets the seconds to wait for a connection per attempt.
        /// </summary>
        public int ConnectTimeout { get; set; } = 5;

        /// <summary>
        /// Gets or sets a value indicating whether playback sessions are spread over
        /// several hardware classes (experimental). Takes effect after a server restart.
        /// </summary>
        public bool EnableHardwareClasses { get; set; }

        /// <summary>
        /// Gets or sets the class new playback sessions use: a class name ("intel",
        /// "nvidia"), <see cref="HardwareClassSettings.Auto"/> to choose by media and load,
        /// <see cref="HardwareClassSettings.Alternate"/> to take turns, or empty for
        /// Jellyfin's own transcoding settings.
        /// </summary>
        public string DefaultHardwareClass { get; set; } = HardwareClassSettings.Auto;

        public HardwareClassSettings IntelClass { get; set; } = new() { Name = HardwareClassSettings.Intel, GrpcHost = "ffmpeg-workers-intel" };

        public HardwareClassSettings NvidiaClass { get; set; } = new() { Name = HardwareClassSettings.Nvidia, GrpcHost = "ffmpeg-workers-nvidia" };

        /// <summary>
        /// Gets the hardware classes. Each one's name comes from its setting, whatever the
        /// saved file says: the name selects the class's hardware arguments and its entry in
        /// CLASS_ADDRESSES, so an edited or missing one must not break routing.
        /// </summary>
        public IEnumerable<HardwareClassSettings> HardwareClasses()
        {
            IntelClass ??= new() { GrpcHost = "ffmpeg-workers-intel" };
            NvidiaClass ??= new() { GrpcHost = "ffmpeg-workers-nvidia" };
            IntelClass.Name = HardwareClassSettings.Intel;
            NvidiaClass.Name = HardwareClassSettings.Nvidia;
            return new[] { IntelClass, NvidiaClass };
        }
    }
}
