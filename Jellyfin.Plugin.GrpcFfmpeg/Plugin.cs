using Jellyfin.Plugin.GrpcFfmpeg.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.GrpcFfmpeg
{
    /// <summary>
    /// Runs Jellyfin's ffmpeg commands on remote grpc-ffmpeg workers.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        private readonly IApplicationPaths _applicationPaths;
        private readonly IConfiguration _startupConfig;
        private readonly IServerConfigurationManager _configurationManager;
        private readonly ILogger<Plugin> _logger;

        public Plugin(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer,
            IConfiguration startupConfig,
            IServerConfigurationManager configurationManager,
            ILogger<Plugin> logger)
            : base(applicationPaths, xmlSerializer)
        {
            _applicationPaths = applicationPaths;
            _startupConfig = startupConfig;
            _configurationManager = configurationManager;
            _logger = logger;
            Instance = this;

            // Keep the deployed client in sync with this plugin version
            Prepare();
        }

        public static Plugin? Instance { get; private set; }

        public override Guid Id => Guid.Parse("5FCE29C6-1366-41CD-9B05-6447A531B590");

        public override string Name => "gRPC-ffmpeg";

        public override string Description => "Runs Jellyfin's ffmpeg commands on remote grpc-ffmpeg workers.";

        public string DeployDirectory => Relay.DeployDirectory(_applicationPaths);

        public string? FallbackDirectory => Relay.FallbackDirectory(Configuration, _startupConfig, _configurationManager, DeployDirectory);

        /// <summary>
        /// Deploys the client and writes its config file. Never throws, as this runs
        /// during server startup.
        /// </summary>
        /// <returns>The path to use as ffmpeg, or null on failure.</returns>
        public string? Prepare()
        {
            try
            {
                return Relay.Prepare(_applicationPaths, Configuration, _startupConfig, _configurationManager, _logger);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "gRPC-ffmpeg: failed to deploy the client to {Directory}", DeployDirectory);
                return null;
            }
        }

        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            base.UpdateConfiguration(configuration);
            // Worker settings apply to the next command right away; Enabled needs a restart
            Prepare();
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "gRPC-ffmpeg",
                    EmbeddedResourcePath = GetType().Namespace + ".Web.config.html",
                },
            };
        }
    }
}
