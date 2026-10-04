using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.GrpcFfmpeg.Configuration
{
    /// <summary>
    /// A hardware class (experimental): a worker pool with one kind of GPU, and the
    /// transcoding settings that differ from Jellyfin's for it.
    /// </summary>
    public class HardwareClassSettings
    {
        public const string Intel = "intel";

        public const string Nvidia = "nvidia";

        /// <summary>Default class value: new sessions take turns between the enabled classes.</summary>
        public const string Alternate = "alternate";

        /// <summary>
        /// Gets or sets the class name, as used in the client's CLASS_ADDRESSES.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        public bool Enabled { get; set; }

        public string GrpcHost { get; set; } = string.Empty;

        public int GrpcPort { get; set; } = 50051;

        /// <summary>
        /// Gets or sets the QSV render node (Intel only); empty keeps Jellyfin's setting.
        /// </summary>
        public string Device { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the codecs decoded in hardware, as in Jellyfin's transcoding settings.
        /// </summary>
        public string[] HardwareDecodingCodecs { get; set; } = { "h264", "hevc", "mpeg2video", "vc1", "vp8", "vp9", "av1" };

        public bool EnableDecodingColorDepth10Hevc { get; set; } = true;

        public bool EnableDecodingColorDepth10Vp9 { get; set; } = true;

        public bool EnableTonemapping { get; set; }

        public bool AllowHevcEncoding { get; set; }

        public bool AllowAv1Encoding { get; set; }

        /// <summary>
        /// Gets the hardware acceleration type Jellyfin uses for this class.
        /// </summary>
        public HardwareAccelerationType AccelerationType =>
            Name == Nvidia ? HardwareAccelerationType.nvenc : HardwareAccelerationType.qsv;

        /// <summary>
        /// Gets the address of the class's workers, or null if the class cannot be used.
        /// </summary>
        public string? Address
        {
            get
            {
                var host = GrpcHost.Trim();
                if (!Enabled || host.Length == 0 || GrpcPort is <= 0 or >= 65536)
                {
                    return null;
                }

                // IPv6 addresses need brackets before the port
                return host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[')
                    ? FormattableString.Invariant($"[{host}]:{GrpcPort}")
                    : FormattableString.Invariant($"{host}:{GrpcPort}");
            }
        }
    }
}
