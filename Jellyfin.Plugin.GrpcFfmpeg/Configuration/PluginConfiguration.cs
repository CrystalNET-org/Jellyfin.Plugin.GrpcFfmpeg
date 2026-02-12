using System.Xml.Serialization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.GrpcFfmpeg.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public string DeployPath { get; set; } = "/var/lib/jellyfin/grpc-ffmpeg";
        public bool SymlinkFFmpeg { get; set; } = true;
        public bool SymlinkFFprobe { get; set; } = true;

        public bool UseSsl { get; set; } = false;
        public string GrpcHost { get; set; } = "ffmpeg-workers";
        public int GrpcPort { get; set; } = 50051;
        public string CertificatePath { get; set; } = "server.crt";
        public string AuthToken { get; set; } = "my_secret_token1";

        // EnvVars list removed
    }
}
