using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Jellyfin.Plugin.GrpcFfmpeg.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.GrpcFfmpeg.Managers
{
    public class DeploymentManager
    {
        private readonly string _deployPath;
        private readonly bool _isWindows;
        private readonly ILogger<DeploymentManager> _logger;

        public DeploymentManager(IApplicationPaths appPaths, ILogger<DeploymentManager> logger)
        {
            _deployPath = Path.Combine(appPaths.ProgramDataPath, "grpc-ffmpeg");
            _isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            _logger = logger;
            _logger.LogInformation("DeploymentManager initialized. DeployPath: {DeployPath}, IsWindows: {IsWindows}", _deployPath, _isWindows);
        }

        public void Deploy(PluginConfiguration config)
        {
            _logger.LogInformation("Starting deployment with config: {@Config}", config);
            Directory.CreateDirectory(_deployPath);

            var (resourceName, binaryName) = GetPlatformAwareBinaryNames();
            var binaryPath = Path.Combine(_deployPath, binaryName);
            ExtractEmbeddedResource(resourceName, binaryPath);
            if (!_isWindows)
            {
                MakeExecutable(binaryPath);
            }

            // Hardcode to always create these wrappers
            UpdateWrapper("ffmpeg", true, binaryName);
            UpdateWrapper("ffprobe", true, binaryName);
            UpdateWrapper("mediainfo", true, binaryName);
            UpdateWrapper("vainfo", true, binaryName);
            _logger.LogInformation("Deployment completed.");
        }
        
        private void UpdateWrapper(string wrapperName, bool shouldExist, string targetBinaryName)
        {
            var wrapperPath = Path.Combine(_deployPath, _isWindows ? $"{wrapperName}.exe" : wrapperName);
            _logger.LogInformation("Updating wrapper {WrapperName}. Path: {WrapperPath}, ShouldExist: {ShouldExist}", wrapperName, wrapperPath, shouldExist);

            if (shouldExist) // This will always be true now for hardcoded wrappers
            {
                if (_isWindows)
                {
                    _logger.LogInformation("Creating Windows wrapper (copy) at {WrapperPath} pointing to {TargetBinaryName}", wrapperPath, targetBinaryName);
                    File.Copy(Path.Combine(_deployPath, targetBinaryName), wrapperPath, true);
                }
                else
                {
                    _logger.LogInformation("Creating Linux symlink at {WrapperPath} pointing to {TargetBinaryName}", wrapperPath, targetBinaryName);
                    CreateLinuxSymlink(wrapperPath, targetBinaryName);
                }
            }
            else // This block will no longer be reached for the hardcoded wrappers
            {
                if (File.Exists(wrapperPath))
                {
                    _logger.LogInformation("Wrapper {WrapperName} should not exist. Deleting {WrapperPath}", wrapperName, wrapperPath);
                    try
                    {
                        File.Delete(wrapperPath);
                        _logger.LogInformation("Successfully deleted {WrapperPath}", wrapperPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to delete {WrapperPath}", wrapperPath);
                    }
                }
                else
                {
                    _logger.LogInformation("Wrapper {WrapperName} does not exist at {WrapperPath}, no deletion needed.", wrapperName, wrapperPath);
                }
            }
        }
        
        private (string resourceName, string binaryName) GetPlatformAwareBinaryNames()
        {
            string os, arch, ext;
            if (_isWindows)
            {
                os = "windows";
                ext = ".exe";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                os = "linux";
                ext = "";
            }
            else
            {
                throw new PlatformNotSupportedException("Only Linux and Windows are supported.");
            }

            switch (RuntimeInformation.ProcessArchitecture)
            {
                case Architecture.X64:
                    arch = "amd64";
                    break;
                case Architecture.Arm64:
                    arch = "arm64";
                    break;
                default:
                    throw new PlatformNotSupportedException($"Unsupported architecture: {RuntimeInformation.ProcessArchitecture}");
            }

            var resourceName = $"Jellyfin.Plugin.GrpcFfmpeg.Assets.grpc-ffmpeg-{os}-{arch}{ext}";
            var binaryName = $"grpc-ffmpeg{ext}";

            return (resourceName, binaryName);
        }

        private void ExtractEmbeddedResource(string resourceName, string destinationPath)
        {
            _logger.LogInformation("Extracting embedded resource {ResourceName} to {DestinationPath}", resourceName, destinationPath);
            using var resourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (resourceStream == null)
            {
                _logger.LogError("Embedded resource not found: {ResourceName}", resourceName);
                throw new FileNotFoundException($"Embedded resource not found: {resourceName}.");
            }
            using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write);
            resourceStream.CopyTo(fileStream);
            _logger.LogInformation("Successfully extracted embedded resource.");
        }

        private void MakeExecutable(string path)
        {
            _logger.LogInformation("Making {Path} executable.", path);
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                { 
                    FileName = "/bin/chmod", 
                    Arguments = $"+x \"{path}\"", 
                    RedirectStandardOutput = true, 
                    RedirectStandardError = true, 
                    UseShellExecute = false, 
                    CreateNoWindow = true 
                }
            };
            process.Start();
            process.WaitForExit();
            _logger.LogInformation("chmod process exited with code {ExitCode}. StdOut: {StdOut}, StdErr: {StdErr}", process.ExitCode, process.StandardOutput.ReadToEnd(), process.StandardError.ReadToEnd());
        }

        private void CreateLinuxSymlink(string symlinkPath, string targetName)
        {
            _logger.LogInformation("Creating Linux symlink from {SymlinkPath} to {TargetName}", symlinkPath, targetName);
            if (File.Exists(symlinkPath))
            {
                _logger.LogInformation("Existing symlink found at {SymlinkPath}, deleting.", symlinkPath);
                File.Delete(symlinkPath);
            }
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/bin/ln",
                    Arguments = $"-sf \"{targetName}\" \"{symlinkPath}\"",
                    WorkingDirectory = Path.GetDirectoryName(symlinkPath),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            process.WaitForExit();
            _logger.LogInformation("ln process exited with code {ExitCode}. StdOut: {StdOut}, StdErr: {StdErr}", process.ExitCode, process.StandardOutput.ReadToEnd(), process.StandardError.ReadToEnd());
        }
    }
}