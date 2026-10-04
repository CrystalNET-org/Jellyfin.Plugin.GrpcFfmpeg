using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.GrpcFfmpeg.Configuration;
using Jellyfin.Plugin.GrpcFfmpeg.HardwareClasses;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Configuration;

namespace Jellyfin.Plugin.GrpcFfmpeg
{
    /// <summary>
    /// Checks the setup through the workers: connection, ffmpeg version and shared paths. It
    /// tests the given settings, which need not be saved yet, so the settings page can test
    /// what is entered.
    /// </summary>
    internal sealed partial class SetupCheck
    {
        /// <summary>
        /// Settings of the client that, set in Jellyfin's environment, override the plugin's.
        /// </summary>
        public static readonly string[] ClientEnvironmentVariables =
        {
            "GRPC_FFMPEG_CONFIG", "GRPC_HOST", "GRPC_PORT", "AUTH_TOKEN", "USE_SSL", "CERTIFICATE_PATH",
            "FALLBACK_DIR", "RETRIES", "CONNECT_TIMEOUT", "LOG_FILE", "CLASS_ADDRESSES",
        };

        private static readonly TimeSpan _commandTimeout = TimeSpan.FromSeconds(30);

        // 1x1 PNG, copied by the workers to check a directory
        private static readonly byte[] _png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

        private static readonly HashSet<string> _mediaExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".ts", ".m2ts", ".wmv", ".webm", ".mpg", ".mpeg",
            ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav",
        };

        private readonly string _deployDirectory;
        private readonly PluginConfiguration _config;
        private readonly IServerConfigurationManager _configurationManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IConfiguration _startupConfig;

        public SetupCheck(
            string deployDirectory,
            PluginConfiguration config,
            IServerConfigurationManager configurationManager,
            ILibraryManager libraryManager,
            IConfiguration startupConfig)
        {
            _deployDirectory = deployDirectory;
            _config = config;
            _configurationManager = configurationManager;
            _libraryManager = libraryManager;
            _startupConfig = startupConfig;
        }

        /// <summary>
        /// Gets the client settings set in Jellyfin's environment.
        /// </summary>
        public static List<string> OverridingEnvironmentVariables() =>
            ClientEnvironmentVariables.Where(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))).ToList();

        /// <summary>
        /// Tests the default workers: everything that does not use a hardware class.
        /// </summary>
        public async Task<TestResult> RunAsync()
        {
            var version = await RunClientAsync("ffmpeg", new[] { "-hide_banner", "-version" }).ConfigureAwait(false);
            if (version.ExitCode != 0)
            {
                return new TestResult(false, version.ExitCode, version.Error, new List<CheckResult>(), new List<string>());
            }

            var warnings = new List<string>();
            var versionWarning = await CheckVersionAsync(version.Output).ConfigureAwait(false);
            if (versionWarning is not null)
            {
                warnings.Add(versionWarning);
            }

            var checks = new List<CheckResult>
            {
                await CheckDirectoryAsync("Transcode directory", () => _configurationManager.GetTranscodePath()).ConfigureAwait(false),
                await CheckDirectoryAsync("Temp directory (image extraction, trickplay)", () => _configurationManager.ApplicationPaths.TempDirectory).ConfigureAwait(false),
            };
            checks.AddRange(await CheckLibrariesAsync().ConfigureAwait(false));

            return new TestResult(true, 0, version.Output, checks, warnings);
        }

        /// <summary>
        /// Warns if the workers' ffmpeg has another major version than the local one, which
        /// Jellyfin detects while the workers are down.
        /// </summary>
        private async Task<string?> CheckVersionAsync(string workerVersionOutput)
        {
            var workerMajor = MajorVersion(workerVersionOutput);
            var localDirectory = !string.IsNullOrWhiteSpace(_config.FallbackDirectory)
                ? _config.FallbackDirectory.Trim()
                : Path.GetDirectoryName(Relay.OriginalFfmpegPath(_startupConfig, _configurationManager, _deployDirectory) ?? string.Empty);
            if (workerMajor is null || string.IsNullOrEmpty(localDirectory))
            {
                return null;
            }

            var localFfmpeg = Path.Combine(localDirectory, "ffmpeg" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
            if (!File.Exists(localFfmpeg))
            {
                return null;
            }

            var local = await RunAsync(localFfmpeg, new[] { "-hide_banner", "-version" }, new Dictionary<string, string>()).ConfigureAwait(false);
            var localMajor = local.ExitCode == 0 ? MajorVersion(local.Output) : null;
            if (localMajor is null || localMajor == workerMajor)
            {
                return null;
            }

            return $"The workers run ffmpeg {workerMajor}, the local ffmpeg ({localFfmpeg}) is version {localMajor}. "
                + "Jellyfin picks ffmpeg options by the version it detects at startup, and detects the local one while the workers are down. "
                + $"Use workers with ffmpeg {localMajor}.";
        }

        /// <summary>
        /// Has the workers copy a file Jellyfin wrote into the directory, and checks that the copy
        /// appears here: the directory must be shared, readable and writable for the workers.
        /// </summary>
        /// <param name="name">Name of the check.</param>
        /// <param name="getDirectory">Gets the directory.</param>
        /// <param name="environment">Client settings for the workers to check, null for the default ones.</param>
        private async Task<CheckResult> CheckDirectoryAsync(string name, Func<string> getDirectory, Dictionary<string, string>? environment = null)
        {
            string directory;
            try
            {
                directory = getDirectory();
            }
            catch (Exception ex)
            {
                return new CheckResult(name, string.Empty, false, $"Could not be determined: {ex.Message}");
            }

            var id = Guid.NewGuid().ToString("N");
            var input = Path.Combine(directory, $"grpc-ffmpeg-check-{id}.png");
            var output = Path.Combine(directory, $"grpc-ffmpeg-check-{id}-copy.png");
            try
            {
                Directory.CreateDirectory(directory);
                await File.WriteAllBytesAsync(input, _png).ConfigureAwait(false);
                var result = await RunClientAsync(
                    "ffmpeg",
                    new[] { "-hide_banner", "-v", "error", "-i", input, "-frames:v", "1", "-update", "1", "-y", output },
                    environment).ConfigureAwait(false);
                if (result.ExitCode != 0)
                {
                    var error = LastLine(result.Error);
                    return new CheckResult(
                        name,
                        directory,
                        false,
                        error.Contains("No such file or directory", StringComparison.Ordinal)
                            ? $"Not shared: the workers cannot find a file Jellyfin wrote there ({error})"
                            : $"The workers cannot read or write it: {error}");
                }

                return File.Exists(output)
                    ? new CheckResult(name, directory, true, "Shared with the workers")
                    : new CheckResult(name, directory, false, "The workers write to a different directory at this path: it is not shared");
            }
            catch (Exception ex)
            {
                return new CheckResult(name, directory, false, $"Could not be checked: {ex.Message}");
            }
            finally
            {
                TryDelete(input);
                TryDelete(output);
            }
        }

        /// <summary>
        /// Has the workers probe one media file of each library.
        /// </summary>
        private async Task<List<CheckResult>> CheckLibrariesAsync()
        {
            var results = new List<CheckResult>();
            List<MediaBrowser.Model.Entities.VirtualFolderInfo> folders;
            try
            {
                folders = _libraryManager.GetVirtualFolders();
            }
            catch (Exception ex)
            {
                results.Add(new CheckResult("Libraries", string.Empty, false, $"Could not be listed: {ex.Message}"));
                return results;
            }

            foreach (var folder in folders.Take(20))
            {
                var name = $"Library \"{folder.Name}\"";
                var file = FindMediaFile(folder);
                if (file is null)
                {
                    results.Add(new CheckResult(name, string.Join(", ", folder.Locations ?? Array.Empty<string>()), true, "Skipped: no media file found to check"));
                    continue;
                }

                var result = await RunClientAsync("ffprobe", new[] { "-hide_banner", "-v", "error", "-show_entries", "format=format_name", "-of", "csv=p=0", file }).ConfigureAwait(false);
                results.Add(result.ExitCode == 0
                    ? new CheckResult(name, file, true, "Readable by the workers")
                    : new CheckResult(name, file, false, $"The workers cannot read it: {LastLine(result.Error)}"));
            }

            return results;
        }

        private static string? FindMediaFile(MediaBrowser.Model.Entities.VirtualFolderInfo folder) =>
            (folder.Locations ?? Array.Empty<string>()).Select(FindMediaFile).FirstOrDefault(path => path is not null);

        private static string? FindMediaFile(string location)
        {
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                return Directory.EnumerateFiles(location, "*", options)
                    .Take(5000)
                    .FirstOrDefault(path => _mediaExtensions.Contains(Path.GetExtension(path)));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Tests a hardware class's workers (experimental): that they answer, which codecs
        /// their GPU encodes, and that they share the transcode directory and can read the
        /// media, as they run the transcodes of the class's sessions.
        /// </summary>
        public async Task<TestResult> RunClassAsync(HardwareClassSettings hardwareClass)
        {
            if (hardwareClass.AddressError is { } addressError)
            {
                return new TestResult(false, 1, addressError, new List<CheckResult>(), new List<string>());
            }

            var environment = ClassEnvironment(hardwareClass);
            var probe = await ClassProbe.RunAsync(hardwareClass, (name, arguments) => RunClientAsync(name, arguments, environment)).ConfigureAwait(false);
            if (!probe.Reachable)
            {
                return new TestResult(false, 1, probe.Error ?? "No answer", new List<CheckResult>(), new List<string>());
            }

            var checks = new List<CheckResult>
            {
                probe.GpuUsable
                    ? new CheckResult("GPU", hardwareClass.AccelerationType.ToString(), true, "Encodes " + string.Join(", ", probe.Encoders.Select(CodecName)))
                    : new CheckResult("GPU", hardwareClass.AccelerationType.ToString(), false, probe.Error ?? "Not usable"),
                await CheckDirectoryAsync("Transcode directory", () => _configurationManager.GetTranscodePath(), environment).ConfigureAwait(false),
            };

            string? mediaFile = null;
            try
            {
                mediaFile = _libraryManager.GetVirtualFolders().Take(20).Select(FindMediaFile).FirstOrDefault(path => path is not null);
            }
            catch (Exception)
            {
                // Reported by the default workers' test
            }

            if (mediaFile is not null)
            {
                var result = await RunClientAsync(
                    "ffprobe",
                    new[] { "-hide_banner", "-v", "error", "-show_entries", "format=format_name", "-of", "csv=p=0", mediaFile },
                    environment).ConfigureAwait(false);
                checks.Add(result.ExitCode == 0
                    ? new CheckResult("Media", mediaFile, true, "Readable by the workers")
                    : new CheckResult("Media", mediaFile, false, $"The workers cannot read it: {LastLine(result.Error)}"));
            }

            return new TestResult(true, 0, probe.Version ?? string.Empty, checks, new List<string>(), probe.Encoders);
        }

        /// <summary>
        /// Gets the client settings that send every command to the class's workers: the
        /// probe and check commands have no hardware arguments, or must not be routed by them.
        /// They stay out of the activity log, whose lines count how commands were routed.
        /// </summary>
        public static Dictionary<string, string> ClassEnvironment(HardwareClassSettings hardwareClass) => new()
        {
            ["GRPC_HOST"] = hardwareClass.GrpcHost.Trim().Trim('[', ']'),
            ["GRPC_PORT"] = hardwareClass.GrpcPort.ToString(CultureInfo.InvariantCulture),
            ["CLASS_ADDRESSES"] = string.Empty,
            ["LOG_FILE"] = string.Empty,
        };

        /// <summary>
        /// Gets the client settings for the given plugin settings, so that unsaved settings
        /// are tested rather than the client's config file.
        /// </summary>
        public static Dictionary<string, string> ConnectionEnvironment(PluginConfiguration config) => new()
        {
            ["GRPC_HOST"] = config.GrpcHost?.Trim() ?? string.Empty,
            ["GRPC_PORT"] = config.GrpcPort.ToString(CultureInfo.InvariantCulture),
            ["AUTH_TOKEN"] = config.AuthToken ?? string.Empty,
            ["USE_SSL"] = config.UseSsl ? "true" : "false",
            ["CERTIFICATE_PATH"] = config.CertificatePath?.Trim() ?? string.Empty,
            ["CONNECT_TIMEOUT"] = Math.Max(1, config.ConnectTimeout).ToString(CultureInfo.InvariantCulture),
        };

        /// <summary>
        /// Runs the deployed client as the given binary, against the workers only: without the
        /// fallback and with a single attempt.
        /// </summary>
        public static Task<CommandResult> RunClientAsync(string deployDirectory, string name, IEnumerable<string> arguments, Dictionary<string, string>? environment = null)
        {
            var path = Path.Combine(deployDirectory, name + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
            var settings = new Dictionary<string, string> { ["FALLBACK_DIR"] = string.Empty, ["RETRIES"] = "1" };
            foreach (var (key, value) in environment ?? new Dictionary<string, string>())
            {
                settings[key] = value;
            }

            return RunAsync(path, arguments, settings);
        }

        private static string CodecName(string codec) => codec switch
        {
            "h264" => "H.264",
            "hevc" => "HEVC",
            "av1" => "AV1",
            _ => codec,
        };

        private Task<CommandResult> RunClientAsync(string name, IEnumerable<string> arguments, Dictionary<string, string>? environment = null)
        {
            var settings = ConnectionEnvironment(_config);
            foreach (var (key, value) in environment ?? new Dictionary<string, string>())
            {
                settings[key] = value;
            }

            return RunClientAsync(_deployDirectory, name, arguments, settings);
        }

        private static async Task<CommandResult> RunAsync(string path, IEnumerable<string> arguments, Dictionary<string, string> environment)
        {
            var startInfo = new ProcessStartInfo(path)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }

            using var process = Process.Start(startInfo)!;
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(_commandTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return new CommandResult(-1, string.Empty, $"No answer within {_commandTimeout.TotalSeconds} seconds");
            }

            return new CommandResult(process.ExitCode, (await stdout.ConfigureAwait(false)).Trim(), (await stderr.ConfigureAwait(false)).Trim());
        }

        private static int? MajorVersion(string versionOutput)
        {
            var match = VersionRegex().Match(versionOutput);
            return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
        }

        private static string LastLine(string text)
        {
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return lines.Length > 0 ? lines[^1] : "no error output";
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
                // Left for Jellyfin's cleanup of the directory
            }
        }

        [GeneratedRegex(@"version\s+n?(\d+)\.")]
        private static partial Regex VersionRegex();

    }

    /// <summary>
    /// Exit code and output of a command.
    /// </summary>
    internal sealed record CommandResult(int ExitCode, string Output, string Error);

    /// <summary>
    /// Result of one check of the setup.
    /// </summary>
    internal sealed record CheckResult(string Name, string Path, bool Ok, string Message);

    /// <summary>
    /// Result of the setup test.
    /// </summary>
    /// <param name="Encoders">For a hardware class: the codecs its GPU encodes.</param>
    internal sealed record TestResult(bool Success, int ExitCode, string Output, List<CheckResult> Checks, List<string> Warnings, IReadOnlyList<string>? Encoders = null);
}
