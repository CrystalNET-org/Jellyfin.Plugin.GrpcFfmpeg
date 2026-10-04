using Jellyfin.Plugin.GrpcFfmpeg.Configuration;
using Jellyfin.Plugin.GrpcFfmpeg.HardwareClasses;
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
        private readonly SemaphoreSlim _detection = new(1, 1);

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

            if (Configuration.EnableHardwareClasses && PluginServiceRegistrator.HardwareClassesUnavailable is { } reason)
            {
                _logger.LogError("gRPC-ffmpeg: hardware classes are unavailable with this Jellyfin version, all sessions use Jellyfin's settings: {Reason}", reason);
            }

            // Once the server is up, rather than delaying its start
            DetectClassEncoders(TimeSpan.FromSeconds(30));
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
            DetectClassEncoders(TimeSpan.Zero);
        }

        /// <summary>
        /// Checks in the background which codecs the enabled hardware classes' GPUs encode,
        /// and saves that as their HEVC and AV1 encoding settings. Results for workers that
        /// cannot be reached, or whose GPU does not work, leave the settings as they are.
        /// </summary>
        private void DetectClassEncoders(TimeSpan delay)
        {
            if (!Configuration.EnableHardwareClasses)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                    // One detection at a time; a later one sees the newer settings
                    await _detection.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        foreach (var hardwareClass in Configuration.HardwareClasses().Where(c => c.Address is not null).ToList())
                        {
                            var environment = SetupCheck.ConnectionEnvironment(Configuration);
                            foreach (var (key, value) in SetupCheck.ClassEnvironment(hardwareClass))
                            {
                                environment[key] = value;
                            }

                            var result = await ClassProbe.RunAsync(
                                hardwareClass,
                                (name, arguments) => SetupCheck.RunClientAsync(DeployDirectory, name, arguments, environment)).ConfigureAwait(false);
                            if (!result.GpuUsable)
                            {
                                _logger.LogWarning("gRPC-ffmpeg: could not check hardware class {Class} ({Address}): {Error}", result.Class, result.Address, result.Error);
                                continue;
                            }

                            _logger.LogInformation("gRPC-ffmpeg: hardware class {Class} ({Address}) encodes {Codecs}", result.Class, result.Address, string.Join(", ", result.Encoders));
                            // The settings may have been saved meanwhile: apply to the current ones
                            var current = Configuration;
                            if (ClassProbe.Apply(current, result))
                            {
                                // Not UpdateConfiguration, which would start another detection
                                SaveConfiguration(current);
                            }
                        }
                    }
                    finally
                    {
                        _detection.Release();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "gRPC-ffmpeg: checking the hardware classes' encoders failed");
                }
            });
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "gRPC-ffmpeg",
                    EmbeddedResourcePath = GetType().Namespace + ".Web.config.html",
                    // Linked in the dashboard sidebar, below Plugins
                    EnableInMainMenu = true,
                    MenuIcon = "memory",
                },
            };
        }
    }
}
