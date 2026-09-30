using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.GrpcFfmpeg.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.GrpcFfmpeg
{
    /// <summary>
    /// Deploys the grpc-ffmpeg client and its configuration file.
    /// </summary>
    internal static class Relay
    {
        /// <summary>Client config file, read by the client from its own directory.</summary>
        public const string ClientConfigFileName = "grpc-ffmpeg.conf";

        /// <summary>
        /// Names the client is installed as. It runs the binary it is invoked as;
        /// Jellyfin uses ffmpeg and ffprobe, some plugins also mediainfo and vainfo.
        /// </summary>
        private static readonly string[] _wrapperNames = { "ffmpeg", "ffprobe", "mediainfo", "vainfo" };

        private static readonly object _deployLock = new();

        private static string ExecutableSuffix => OperatingSystem.IsWindows() ? ".exe" : string.Empty;

        private static string ClientFileName => "grpc-ffmpeg-client" + ExecutableSuffix;

        public static string DeployDirectory(IApplicationPaths paths) => Path.Combine(paths.ProgramDataPath, "grpc-ffmpeg");

        public static string FfmpegPath(string deployDirectory) => Path.Combine(deployDirectory, "ffmpeg" + ExecutableSuffix);

        /// <summary>
        /// Gets the name of the embedded client for this platform, or null if there is none.
        /// </summary>
        public static string? ClientResourceName()
        {
            string? os = OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "windows" : null;
            string? arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "amd64",
                Architecture.Arm64 => "arm64",
                _ => null,
            };
            if (os is null || arch is null)
            {
                return null;
            }

            var name = $"grpc-ffmpeg-client-{os}-{arch}{ExecutableSuffix}";
            return Assembly.GetExecutingAssembly().GetManifestResourceNames().Contains(name) ? name : null;
        }

        /// <summary>
        /// Deploys the client and config file, and returns the path to use as ffmpeg,
        /// or null if this platform has no client.
        /// </summary>
        public static string? Prepare(
            IApplicationPaths paths,
            PluginConfiguration config,
            IConfiguration startupConfig,
            IServerConfigurationManager configurationManager,
            ILogger logger)
        {
            var directory = DeployDirectory(paths);
            lock (_deployLock)
            {
                if (!Deploy(directory, logger))
                {
                    return null;
                }

                ActivityConsole.Start(directory, logger);
                WriteClientConfig(directory, config, FallbackDirectory(config, startupConfig, configurationManager, directory), logger);
            }

            return FfmpegPath(directory);
        }

        /// <summary>
        /// Gets the directory of the local ffmpeg to fall back to, or null if disabled or unknown.
        /// </summary>
        public static string? FallbackDirectory(
            PluginConfiguration config,
            IConfiguration startupConfig,
            IServerConfigurationManager configurationManager,
            string deployDirectory)
        {
            if (!config.EnableFallback)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(config.FallbackDirectory))
            {
                return config.FallbackDirectory.Trim();
            }

            var original = OriginalFfmpegPath(startupConfig, configurationManager, deployDirectory);
            return original is null ? null : Path.GetDirectoryName(original);
        }

        /// <summary>
        /// Gets the ffmpeg Jellyfin would use without this plugin, in Jellyfin's order:
        /// --ffmpeg / JELLYFIN_FFMPEG, then encoding.xml, then $PATH.
        /// </summary>
        public static string? OriginalFfmpegPath(
            IConfiguration startupConfig,
            IServerConfigurationManager configurationManager,
            string deployDirectory)
        {
            var candidates = new List<string?>
            {
                startupConfig[MediaBrowser.Controller.Extensions.ConfigurationExtensions.FfmpegPathKey],
                configurationManager.GetConfiguration<EncodingOptions>("encoding").EncoderAppPath,
            };
            candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(dir => Path.Combine(dir, "ffmpeg" + ExecutableSuffix)));

            return candidates.FirstOrDefault(path =>
                !string.IsNullOrWhiteSpace(path)
                && File.Exists(path)
                && !IsInDirectory(path, deployDirectory));
        }

        private static bool IsInDirectory(string path, string directory) =>
            string.Equals(
                Path.GetFullPath(Path.GetDirectoryName(path) ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        /// <summary>
        /// Extracts the embedded client and installs it under the wrapper names.
        /// </summary>
        private static bool Deploy(string directory, ILogger logger)
        {
            var resourceName = ClientResourceName();
            if (resourceName is null)
            {
                logger.LogWarning(
                    "No grpc-ffmpeg client is available for {Os} {Architecture}",
                    RuntimeInformation.OSDescription,
                    RuntimeInformation.ProcessArchitecture);
                return false;
            }

            Directory.CreateDirectory(directory);
            byte[] client;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)!)
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                client = buffer.ToArray();
            }

            var clientPath = Path.Combine(directory, ClientFileName);
            if (WriteIfChanged(clientPath, client))
            {
                logger.LogInformation("Deployed grpc-ffmpeg client to {Path}", clientPath);
            }

            foreach (var name in _wrapperNames)
            {
                var wrapperPath = Path.Combine(directory, name + ExecutableSuffix);
                if (OperatingSystem.IsWindows())
                {
                    // No symlinks without special privileges; the client uses its own file name
                    WriteIfChanged(wrapperPath, client);
                }
                else
                {
                    var info = new FileInfo(wrapperPath);
                    if (info.LinkTarget != ClientFileName)
                    {
                        info.Delete();
                        File.CreateSymbolicLink(wrapperPath, ClientFileName);
                    }
                }
            }

            // Leftovers of earlier plugin versions
            File.Delete(Path.Combine(directory, "grpc-ffmpeg" + ExecutableSuffix));
            File.Delete(Path.Combine(directory, "config.yml"));
            return true;
        }

        /// <summary>
        /// Writes the file unless it already has this content. The new file is moved
        /// into place, so running copies of the old binary are not affected.
        /// </summary>
        private static bool WriteIfChanged(string path, byte[] content, UnixFileMode mode = (UnixFileMode)0b111_101_101)
        {
            if (File.Exists(path)
                && new FileInfo(path).Length == content.Length
                && SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(SHA256.HashData(content)))
            {
                return false;
            }

            var temporary = path + ".new";
            File.WriteAllBytes(temporary, content);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary, mode);
            }

            File.Move(temporary, path, overwrite: true);
            return true;
        }

        /// <summary>
        /// Writes the client's config file from the plugin settings.
        /// </summary>
        private static void WriteClientConfig(string directory, PluginConfiguration config, string? fallbackDirectory, ILogger logger)
        {
            var settings = new List<(string Key, string? Value)>
            {
                ("GRPC_HOST", config.GrpcHost),
                ("GRPC_PORT", config.GrpcPort.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("USE_SSL", config.UseSsl ? "true" : "false"),
                ("CERTIFICATE_PATH", config.CertificatePath),
                ("AUTH_TOKEN", config.AuthToken),
                ("RETRIES", Math.Max(1, config.Retries).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("CONNECT_TIMEOUT", Math.Max(1, config.ConnectTimeout).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("FALLBACK_DIR", fallbackDirectory),
                ("LOG_FILE", ActivityConsole.LogTarget(directory)),
            };

            var text = new StringBuilder("# Written by the Jellyfin gRPC-ffmpeg plugin; changes here are overwritten.\n");
            foreach (var (key, value) in settings)
            {
                var clean = value?.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal).Trim();
                if (!string.IsNullOrEmpty(clean))
                {
                    // The client strips one pair of surrounding quotes, so any value round-trips
                    text.Append(key).Append("=\"").Append(clean).Append("\"\n");
                }
            }

            // Contains the auth token: readable by the Jellyfin user only
            if (WriteIfChanged(Path.Combine(directory, ClientConfigFileName), Encoding.UTF8.GetBytes(text.ToString()), UnixFileMode.UserRead | UnixFileMode.UserWrite))
            {
                logger.LogInformation(
                    "Wrote grpc-ffmpeg client config (worker {Host}:{Port}, fallback {Fallback})",
                    config.GrpcHost,
                    config.GrpcPort,
                    fallbackDirectory ?? "disabled");
            }
        }
    }
}
