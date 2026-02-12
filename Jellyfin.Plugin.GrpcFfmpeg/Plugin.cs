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
using MediaBrowser.Controller.Configuration; 
using MediaBrowser.Model.Configuration; 
using MediaBrowser.Controller.MediaEncoding; 
using System.Runtime.InteropServices; 

namespace Jellyfin.Plugin.GrpcFfmpeg
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        private readonly ILogger<Plugin> _logger;
        private readonly ConfigGenerator _configGenerator;
        internal readonly DeploymentManager DeploymentManager; 
        private readonly IServerConfigurationManager _serverConfigurationManager; 
        internal readonly IMediaEncoder MediaEncoder; 
        
        public string DeployPath { get; private set; }

        public override Guid Id => Guid.Parse("5FCE29C6-1366-41CD-9B05-6447A531B590");
        public override string Name => "gRPC-ffmpeg";

        public Plugin(IApplicationPaths applicationPaths, 
                      IXmlSerializer xmlSerializer, 
                      ILogger<Plugin> logger, 
                      ILoggerFactory loggerFactory,
                      IServerConfigurationManager serverConfigurationManager,
                      IMediaEncoder mediaEncoder)
            : base(applicationPaths, xmlSerializer)
        {
            _logger = logger;
            _configGenerator = new ConfigGenerator();
            DeploymentManager = new DeploymentManager(applicationPaths, loggerFactory.CreateLogger<DeploymentManager>()); 
            _serverConfigurationManager = serverConfigurationManager; 
            MediaEncoder = mediaEncoder; 
            
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
            var pluginConfig = (PluginConfiguration)configuration;

            _configGenerator.GenerateGrpcConfig(DeployPath, pluginConfig);

            if (pluginConfig.AutoSetFfmpegPath)
            {
                SetJellyfinFfmpegPath(DeployPath);
            }
            else
            {
                _logger.LogInformation("Auto-setting FFmpeg path is disabled. Manual configuration required if this path was previously set by the plugin.");
            }
        }

        private void SetJellyfinFfmpegPath(string newFfmpegFolderPath) 
        {
            var encodingConfig = _serverConfigurationManager.GetConfiguration<EncodingOptions>("encoding");

            // Simply set to "ffmpeg" and let the OS handle .exe resolution
            string ffmpegExecutableName = "ffmpeg"; 
            string newFfmpegPath = Path.Combine(newFfmpegFolderPath, ffmpegExecutableName);

            if (encodingConfig.EncoderAppPath != newFfmpegPath)
            {
                _logger.LogInformation($"gRPC Ffmpeg Plugin: Overwriting FFmpeg path from '{encodingConfig.EncoderAppPath}' to '{newFfmpegPath}'");
                encodingConfig.EncoderAppPath = newFfmpegPath;
                _serverConfigurationManager.SaveConfiguration("encoding", encodingConfig);
                _logger.LogInformation("gRPC Ffmpeg Plugin: FFmpeg path updated in Jellyfin server configuration.");
            }
            else
            {
                _logger.LogInformation("gRPC Ffmpeg Plugin: FFmpeg path already set to '{NewFfmpegPath}'. No update needed.", newFfmpegPath);
            }
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