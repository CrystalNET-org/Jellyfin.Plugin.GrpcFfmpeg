using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.GrpcFfmpeg.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.GrpcFfmpeg
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        private readonly ILogger<Plugin> _logger;
        public override Guid Id => Guid.Parse("5FCE29C6-1366-41CD-9B05-6447A531B590");

        public override string Name => "gRPC Ffmpeg";

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<Plugin> logger)
            : base(applicationPaths, xmlSerializer)
        {
            _logger = logger;
            _logger.LogInformation("gRPC Ffmpeg Plugin: Constructor - Start");
            Instance = this;
            _logger.LogInformation("gRPC Ffmpeg Plugin: Configuration Path: {Path}", applicationPaths.PluginConfigurationsPath);
            _logger.LogInformation("gRPC Ffmpeg Plugin: Constructor - End");
        }

        public static Plugin? Instance { get; private set; }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            _logger.LogInformation("gRPC Ffmpeg Plugin: GetPages called.");
            
            try
            {
                var jsonConfig = JsonSerializer.Serialize(this.Configuration);
                _logger.LogInformation("gRPC Ffmpeg Plugin: Current configuration state: {Config}", jsonConfig);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "gRPC Ffmpeg Plugin: Failed to serialize configuration for logging.");
            }

            return new[]
            {
                new PluginPageInfo
                {
                    Name = "gRPC Ffmpeg",
                    EmbeddedResourcePath = GetType().Namespace + ".Web.config.html",
                    EnableInMainMenu = true
                }
            };
        }
    }
}
