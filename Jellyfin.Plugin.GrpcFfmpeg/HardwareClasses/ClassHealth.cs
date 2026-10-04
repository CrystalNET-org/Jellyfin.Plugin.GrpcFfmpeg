using System.Text;
using Jellyfin.Plugin.GrpcFfmpeg.Configuration;

namespace Jellyfin.Plugin.GrpcFfmpeg.HardwareClasses
{
    /// <summary>
    /// Whether a class's workers were recently found unreachable, from the marker file the
    /// client writes when a command finds no worker at an address (and removes when one
    /// runs there again). The client only writes it with the fallback enabled.
    /// </summary>
    internal static class ClassHealth
    {
        /// <summary>A marker older than this no longer counts: the workers may be back.</summary>
        public static readonly TimeSpan MarkerValidity = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Gets the client's marker path for the address, named as the client names it.
        /// </summary>
        public static string MarkerPath(string host, int port)
        {
            // The client stores IPv6 hosts without brackets
            var address = new StringBuilder();
            foreach (var c in $"{host.Trim().Trim('[', ']')}_{port}")
            {
                address.Append(char.IsAsciiLetterOrDigit(c) || c == '.' || c == '-' ? c : '_');
            }

            return Path.Combine(Path.GetTempPath(), "grpc-ffmpeg-unreachable-" + address);
        }

        public static bool IsUnreachable(HardwareClassSettings hardwareClass, DateTime utcNow)
        {
            try
            {
                var marker = new FileInfo(MarkerPath(hardwareClass.GrpcHost, hardwareClass.GrpcPort));
                return marker.Exists && utcNow - marker.LastWriteTimeUtc < MarkerValidity;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
