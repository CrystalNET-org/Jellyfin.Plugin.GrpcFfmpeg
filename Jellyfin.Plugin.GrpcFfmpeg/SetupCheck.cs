using System.Diagnostics;
using System.Text.RegularExpressions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Configuration;

namespace Jellyfin.Plugin.GrpcFfmpeg
{
    /// <summary>
    /// Checks the setup through the workers: connection, ffmpeg version and shared paths.
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
        private readonly IServerConfigurationManager _configurationManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IConfiguration _startupConfig;

        public SetupCheck(
            string deployDirectory,
            IServerConfigurationManager configurationManager,
            ILibraryManager libraryManager,
            IConfiguration startupConfig)
        {
            _deployDirectory = deployDirectory;
            _configurationManager = configurationManager;
            _libraryManager = libraryManager;
            _startupConfig = startupConfig;
        }

        /// <summary>
        /// Gets the client settings set in Jellyfin's environment.
        /// </summary>
        public static List<string> OverridingEnvironmentVariables() =>
            ClientEnvironmentVariables.Where(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))).ToList();

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
            checks.AddRange(await CheckHardwareClassesAsync().ConfigureAwait(false));

            return new TestResult(true, 0, version.Output, checks, warnings);
        }

        /// <summary>
        /// Warns if the workers' ffmpeg has another major version than the local one, which
        /// Jellyfin detects while the workers are down.
        /// </summary>
        private async Task<string?> CheckVersionAsync(string workerVersionOutput)
        {
            var workerMajor = MajorVersion(workerVersionOutput);
            var config = Plugin.Instance?.Configuration;
            var localDirectory = config is not null && !string.IsNullOrWhiteSpace(config.FallbackDirectory)
                ? config.FallbackDirectory.Trim()
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
        private async Task<CheckResult> CheckDirectoryAsync(string name, Func<string> getDirectory)
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
                    new[] { "-hide_banner", "-v", "error", "-i", input, "-frames:v", "1", "-update", "1", "-y", output }).ConfigureAwait(false);
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
                var file = (folder.Locations ?? Array.Empty<string>()).Select(FindMediaFile).FirstOrDefault(path => path is not null);
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
        /// Checks that the workers of each enabled hardware class answer (experimental).
        /// </summary>
        private async Task<List<CheckResult>> CheckHardwareClassesAsync()
        {
            var results = new List<CheckResult>();
            var config = Plugin.Instance?.Configuration;
            if (config is null || !config.EnableHardwareClasses)
            {
                return results;
            }

            foreach (var hardwareClass in config.HardwareClasses().Where(c => c.Enabled))
            {
                var name = $"Hardware class {hardwareClass.Name}";
                if (hardwareClass.Address is null)
                {
                    results.Add(new CheckResult(name, string.Empty, false, "No valid host and port"));
                    continue;
                }

                // -version has no hardware arguments, so it goes to GRPC_HOST: point that at the class
                var result = await RunClientAsync(
                    "ffmpeg",
                    new[] { "-hide_banner", "-version" },
                    new Dictionary<string, string>
                    {
                        ["GRPC_HOST"] = hardwareClass.GrpcHost.Trim(),
                        ["GRPC_PORT"] = hardwareClass.GrpcPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    }).ConfigureAwait(false);
                results.Add(result.ExitCode == 0
                    ? new CheckResult(name, hardwareClass.Address, true, result.Output.Split('\n')[0].Trim())
                    : new CheckResult(name, hardwareClass.Address, false, LastLine(result.Error)));
            }

            return results;
        }

        private Task<CommandResult> RunClientAsync(string name, IEnumerable<string> arguments, Dictionary<string, string>? environment = null)
        {
            var path = Path.Combine(_deployDirectory, name + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
            // Test the workers themselves, not the fallback, and fail fast
            var settings = new Dictionary<string, string> { ["FALLBACK_DIR"] = string.Empty, ["RETRIES"] = "1" };
            foreach (var (key, value) in environment ?? new Dictionary<string, string>())
            {
                settings[key] = value;
            }

            return RunAsync(path, arguments, settings);
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

        private sealed record CommandResult(int ExitCode, string Output, string Error);
    }

    /// <summary>
    /// Result of one check of the setup.
    /// </summary>
    internal sealed record CheckResult(string Name, string Path, bool Ok, string Message);

    /// <summary>
    /// Result of the setup test.
    /// </summary>
    internal sealed record TestResult(bool Success, int ExitCode, string Output, List<CheckResult> Checks, List<string> Warnings);
}
