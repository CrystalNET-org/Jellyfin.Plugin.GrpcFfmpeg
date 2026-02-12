using System.Runtime.InteropServices;
using MediaBrowser.Model.Plugins;

// Removed: using YamlDotNet.Serialization;

namespace Jellyfin.Plugin.GrpcFfmpeg.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        // Wrapper settings
        public bool CreateFfmpeg { get; set; } = true;
        public bool CreateFfprobe { get; set; } = true;
        public bool CreateMediaInfo { get; set; } = true;
        public bool CreateVaInfo { get; set; } = true;

        // gRPC Client settings
        public bool UseSsl { get; set; } = false;
        public string GrpcHost { get; set; } = "ffmpeg-workers";
        public int GrpcPort { get; set; } = 50051;
        public string CertificatePath { get; set; } = "server.crt";
        public string AuthToken { get; set; } = "my_secret_token1";
    }
}