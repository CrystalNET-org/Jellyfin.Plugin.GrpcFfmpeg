using System.Runtime.InteropServices;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.GrpcFfmpeg.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        // Wrapper settings (removed individual Create... flags, these will be hardcoded in DeploymentManager)
        public bool AutoSetFfmpegPath { get; set; } = false;

        // gRPC Client settings
        public bool UseSsl { get; set; } = false;
        public string GrpcHost { get; set; } = "ffmpeg-workers";
        public int GrpcPort { get; set; } = 50051;
        public string CertificatePath { get; set; } = "server.crt";
        public string AuthToken { get; set; } = "my_secret_token1";
    }
}