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
        /// Default class value: by reachability, hardware decoding support for the media,
        /// load and the client's preferred codec.
        /// </summary>
        public const string Auto = "auto";

        /// <summary>
        /// Gets or sets the class name, as used in the client's CLASS_ADDRESSES.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        public bool Enabled { get; set; }

        public string GrpcHost { get; set; } = string.Empty;

        public int GrpcPort { get; set; } = 50051;

        /// <summary>
        /// Gets or sets the relative capacity of the class's workers (e.g. their number of
        /// GPUs), for spreading sessions by load.
        /// </summary>
        public int Weight { get; set; } = 1;

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

        /// <summary>
        /// Gets or sets a value indicating whether HDR is tone mapped to SDR. On by default:
        /// the class's settings replace Jellyfin's, and without it HDR transcodes look washed out.
        /// </summary>
        public bool EnableTonemapping { get; set; } = true;

        public bool AllowHevcEncoding { get; set; }

        public bool AllowAv1Encoding { get; set; }

        /// <summary>
        /// Gets the hardware acceleration type Jellyfin uses for this class.
        /// </summary>
        public HardwareAccelerationType AccelerationType =>
            Name == Nvidia ? HardwareAccelerationType.nvenc : HardwareAccelerationType.qsv;

        /// <summary>
        /// Gets what is wrong with the host and port, or null if they are valid.
        /// </summary>
        public string? AddressError
        {
            get
            {
                var host = (GrpcHost ?? string.Empty).Trim();
                if (host.Length == 0)
                {
                    return "No host set";
                }

                if (GrpcPort is <= 0 or >= 65536)
                {
                    return "The port must be between 1 and 65535";
                }

                // One colon is a host with a port ("workers:50051"), two or more an IPv6 address
                if (host.Count(c => c == ':') == 1 || (host.StartsWith('[') != host.EndsWith(']')))
                {
                    return "The host must not contain a port; set it in the port field";
                }

                return host.Any(c => char.IsWhiteSpace(c) || c is ';' or ',' or '=' or '/')
                    ? "The host contains invalid characters"
                    : null;
            }
        }

        /// <summary>
        /// Gets the address of the class's workers, or null if the class cannot be used.
        /// </summary>
        public string? Address
        {
            get
            {
                var host = (GrpcHost ?? string.Empty).Trim();
                if (!Enabled || AddressError is not null)
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
