using Jellyfin.Plugin.GrpcFfmpeg.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.GrpcFfmpeg
{
    /// <summary>
    /// Makes Jellyfin use the grpc-ffmpeg client as its ffmpeg.
    /// </summary>
    /// <remarks>
    /// Jellyfin takes the ffmpeg path from --ffmpeg / JELLYFIN_FFMPEG before
    /// encoding.xml, and the official images set JELLYFIN_FFMPEG, so changing
    /// encoding.xml has no effect there. The path is read by MediaEncoder from the
    /// IConfiguration it is constructed with; plugin services are registered after
    /// Jellyfin's own, so this registration of IMediaEncoder wins and constructs
    /// Jellyfin's MediaEncoder with the ffmpeg path pointing at the client.
    /// </remarks>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        // Internal to Jellyfin (not on NuGet), so it is looked up at runtime
        private const string MediaEncoderTypeName = "MediaBrowser.MediaEncoding.Encoder.MediaEncoder, MediaBrowser.MediaEncoding";

        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            var encoderType = Type.GetType(MediaEncoderTypeName, throwOnError: false);
            if (encoderType is null || !typeof(IMediaEncoder).IsAssignableFrom(encoderType))
            {
                // Leave Jellyfin's registration alone; the status page reports the relay as inactive
                return;
            }

            serviceCollection.AddSingleton<IMediaEncoder>(services => CreateMediaEncoder(services, encoderType));
        }

        private static IMediaEncoder CreateMediaEncoder(IServiceProvider services, Type encoderType)
        {
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger<PluginServiceRegistrator>();
            try
            {
                var paths = services.GetRequiredService<IApplicationPaths>();
                var config = LoadConfiguration(paths, services.GetRequiredService<IXmlSerializer>());
                if (config.Enabled)
                {
                    var startupConfig = services.GetRequiredService<IConfiguration>();
                    var ffmpegPath = Relay.Prepare(paths, config, startupConfig, services.GetRequiredService<IServerConfigurationManager>(), logger);
                    if (ffmpegPath is not null)
                    {
                        logger.LogInformation("gRPC-ffmpeg: using {FfmpegPath} as ffmpeg", ffmpegPath);
                        var encoderConfig = new ConfigurationBuilder()
                            .AddConfiguration(startupConfig)
                            .AddInMemoryCollection(new Dictionary<string, string?> { [MediaBrowser.Controller.Extensions.ConfigurationExtensions.FfmpegPathKey] = ffmpegPath })
                            .Build();
                        return (IMediaEncoder)ActivatorUtilities.CreateInstance(services, encoderType, encoderConfig);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "gRPC-ffmpeg: setup failed, using Jellyfin's own ffmpeg");
            }

            return (IMediaEncoder)ActivatorUtilities.CreateInstance(services, encoderType);
        }

        /// <summary>
        /// Reads the plugin settings directly, as the plugin itself is created later.
        /// </summary>
        private static PluginConfiguration LoadConfiguration(IApplicationPaths paths, IXmlSerializer xmlSerializer)
        {
            var path = Path.Combine(paths.PluginConfigurationsPath, typeof(Plugin).Assembly.GetName().Name + ".xml");
            return File.Exists(path)
                ? (PluginConfiguration)xmlSerializer.DeserializeFromFile(typeof(PluginConfiguration), path)
                : new PluginConfiguration();
        }
    }
}
