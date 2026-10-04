using Jellyfin.Plugin.GrpcFfmpeg.Configuration;
using Jellyfin.Plugin.GrpcFfmpeg.HardwareClasses;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
            TryRegisterHardwareClasses(serviceCollection, RegisterHardwareClasses);
        }

        /// <summary>
        /// Gets why the experimental hardware classes could not be registered, or null.
        /// </summary>
        public static string? HardwareClassesUnavailable { get; private set; }

        /// <summary>
        /// Registers the experimental hardware classes so that no failure there can cost the
        /// relay: they run on every start, also with the feature off, and a type a future
        /// Jellyfin version no longer has would surface here. The registrations are collected
        /// first and only added if all of them succeeded, so the feature is never half-wired.
        /// </summary>
        internal static void TryRegisterHardwareClasses(IServiceCollection serviceCollection, Action<IServiceCollection, IServiceCollection> register)
        {
            try
            {
                var registrations = new ServiceCollection();
                register(serviceCollection, registrations);
                foreach (var registration in registrations)
                {
                    serviceCollection.Add(registration);
                }

                HardwareClassesUnavailable = null;
            }
            catch (Exception ex)
            {
                // No logger yet at this point; reported at startup and on the status page
                HardwareClassesUnavailable = $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        /// <summary>
        /// Experimental hardware classes: the configuration manager is wrapped so that
        /// streaming requests see their session's class settings, and a middleware sets
        /// the class per request. Both only take effect if the plugin and the hardware
        /// classes were enabled at startup; otherwise Jellyfin's own instance is used.
        /// </summary>
        // Not inlined, so that type load errors in here surface as exceptions in the caller's try
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void RegisterHardwareClasses(IServiceCollection jellyfinServices, IServiceCollection registrations)
        {
            // Jellyfin registers the same instance for both interfaces
            var original = jellyfinServices.LastOrDefault(d => d.ServiceType == typeof(IServerConfigurationManager))?.ImplementationInstance
                as IServerConfigurationManager;
            if (original is null)
            {
                return;
            }

            registrations.AddSingleton(services => new HardwareClassActivation(services, original));
            registrations.AddSingleton<IServerConfigurationManager>(services => services.GetRequiredService<HardwareClassActivation>().ConfigurationManager);
            registrations.AddSingleton<MediaBrowser.Common.Configuration.IConfigurationManager>(services => services.GetRequiredService<HardwareClassActivation>().ConfigurationManager);
            registrations.AddSingleton(CreateSessionClassSelector);
            registrations.AddSingleton<IStartupFilter>(services => services.GetRequiredService<HardwareClassActivation>().Active
                ? ActivatorUtilities.CreateInstance<HardwareClassStartupFilter>(services)
                : new PassThroughStartupFilter());
        }

        /// <summary>
        /// Creates the class selector. Jellyfin's services are resolved on first use, as they
        /// depend on the configuration manager, whose proxy depends on the selector.
        /// </summary>
        private static SessionClassSelector CreateSessionClassSelector(IServiceProvider services)
        {
            SourceVideo? GetSourceVideo(StreamRequest request)
            {
                var id = request.MediaSourceId ?? request.ItemId;
                if (id is null)
                {
                    return null;
                }

                var stream = services.GetRequiredService<IMediaSourceManager>().GetMediaStreams(id.Value)
                    .FirstOrDefault(s => s.Type == MediaStreamType.Video);
                return string.IsNullOrEmpty(stream?.Codec) ? null : new SourceVideo(stream.Codec, stream.BitDepth);
            }

            bool IsTranscoding(string playSessionId) =>
                services.GetService<ITranscodeManager>()?.GetTranscodingJob(playSessionId) is { HasExited: false };

            return new SessionClassSelector(
                () => Plugin.Instance?.Configuration,
                GetSourceVideo,
                IsTranscoding,
                hardwareClass => ClassHealth.IsUnreachable(hardwareClass, DateTime.UtcNow));
        }

        /// <summary>
        /// Decides once, at startup, whether hardware classes are used.
        /// </summary>
        private sealed class HardwareClassActivation
        {
            public HardwareClassActivation(IServiceProvider services, IServerConfigurationManager original)
            {
                ConfigurationManager = original;
                var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger<PluginServiceRegistrator>();
                try
                {
                    var config = LoadConfiguration(services.GetRequiredService<IApplicationPaths>(), services.GetRequiredService<IXmlSerializer>());
                    if (config.Enabled && config.EnableHardwareClasses)
                    {
                        ConfigurationManager = ConfigurationManagerProxy.Create(original, () => HardwareClassContext.Current, logger);
                        Active = true;
                        HardwareClassContext.Active = true;
                        HardwareClassDiagnostics.Logger = logger;
                        logger.LogInformation(
                            "gRPC-ffmpeg: experimental hardware classes enabled ({Classes})",
                            string.Join(", ", config.HardwareClasses().Where(c => c.Address is not null).Select(c => c.Name + "=" + c.Address)));
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "gRPC-ffmpeg: could not enable hardware classes, using Jellyfin's settings for all sessions");
                    ConfigurationManager = original;
                    Active = false;
                }
            }

            public IServerConfigurationManager ConfigurationManager { get; }

            public bool Active { get; }
        }

        private sealed class PassThroughStartupFilter : IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => next;
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
