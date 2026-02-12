using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.GrpcFfmpeg.Configuration;
using Jellyfin.Plugin.GrpcFfmpeg.Managers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace Jellyfin.Plugin.GrpcFfmpeg
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        private readonly ILogger<Plugin> _logger;
        private readonly ConfigGenerator _configGenerator;
        internal readonly DeploymentManager DeploymentManager; // Made internal for GrpcBridgeController access
        
        public string DeployPath { get; private set; }

        public override Guid Id => Guid.Parse("5FCE29C6-1366-41CD-9B05-6447A531B590");
        public override string Name => "gRPC-ffmpeg";

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<Plugin> logger, ILoggerFactory loggerFactory)
            : base(applicationPaths, xmlSerializer)
        {
            _logger = logger;
            _configGenerator = new ConfigGenerator();
            DeploymentManager = new DeploymentManager(applicationPaths, loggerFactory.CreateLogger<DeploymentManager>()); // Initialized here
            DeployPath = Path.Combine(applicationPaths.ProgramDataPath, "grpc-ffmpeg");
            
            Instance = this;
            
            Directory.CreateDirectory(DeployPath);
            
            _configGenerator.GenerateGrpcConfig(DeployPath, this.Configuration);

            _logger.LogInformation("gRPC Ffmpeg Plugin: Listing embedded resources:");
            foreach (var resourceName in Assembly.GetExecutingAssembly().GetManifestResourceNames())
            {
                _logger.LogInformation("- {ResourceName}", resourceName);
            }
        }

        public static Plugin? Instance { get; private set; }

        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            base.UpdateConfiguration(configuration);
            _configGenerator.GenerateGrpcConfig(DeployPath, (PluginConfiguration)configuration);
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "gRPC-ffmpeg",
                    EmbeddedResourcePath = GetType().Namespace + ".Web.config.html",
                    EnableInMainMenu = true
                }
            };
        }
    }
}
