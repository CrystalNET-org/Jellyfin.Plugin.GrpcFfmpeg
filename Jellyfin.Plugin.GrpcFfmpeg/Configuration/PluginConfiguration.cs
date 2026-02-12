using System.Runtime.InteropServices;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.GrpcFfmpeg.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public string DeployPath { get; set; }
        public bool SymlinkFFmpeg { get; set; } = true;
        public bool SymlinkFFprobe { get; set; } = true;
        public bool SymlinkMediaInfo { get; set; } = true;
        public bool SymlinkVaInfo { get; set; } = true;


        public bool UseSsl { get; set; } = false;
        public string GrpcHost { get; set; } = "ffmpeg-workers";
        public int GrpcPort { get; set; } = 50051;
        public string CertificatePath { get; set; } = "server.crt";
        public string AuthToken { get; set; } = "my_secret_token1";

        public PluginConfiguration()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                DeployPath = "C:\\ProgramData\\Jellyfin\\Server\\grpc-ffmpeg";
            }
            else
            {
                DeployPath = "/var/lib/jellyfin/grpc-ffmpeg";
            }
        }
    }
}