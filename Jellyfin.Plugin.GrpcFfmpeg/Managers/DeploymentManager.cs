using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Jellyfin.Plugin.GrpcFfmpeg.Configuration;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.GrpcFfmpeg.Managers
{
    public class DeploymentManager
    {
        private readonly string _deployPath;
        private readonly bool _isWindows;

        public DeploymentManager(IApplicationPaths appPaths)
        {
            _deployPath = Path.Combine(appPaths.ProgramDataPath, "grpc-ffmpeg"); // Corrected Path
            _isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }

        public void Deploy(PluginConfiguration config)
        {
            Directory.CreateDirectory(_deployPath);

            // 1. Deploy the core binary
            var (resourceName, binaryName) = GetPlatformAwareBinaryNames();
            var binaryPath = Path.Combine(_deployPath, binaryName);
            ExtractEmbeddedResource(resourceName, binaryPath);
            if (!_isWindows)
            {
                MakeExecutable(binaryPath);
            }

            // 2. Create/delete copies or symlinks
            UpdateWrapper("ffmpeg", config.CreateFfmpeg, binaryName);
            UpdateWrapper("ffprobe", config.CreateFfprobe, binaryName);
            UpdateWrapper("mediainfo", config.CreateMediaInfo, binaryName);
            UpdateWrapper("vainfo", config.CreateVaInfo, binaryName);
        }
        
        private void UpdateWrapper(string wrapperName, bool shouldExist, string targetBinaryName)
        {
            var wrapperPath = Path.Combine(_deployPath, _isWindows ? $"{wrapperName}.exe" : wrapperName);

            if (shouldExist)
            {
                if (_isWindows)
                {
                    File.Copy(Path.Combine(_deployPath, targetBinaryName), wrapperPath, true);
                }
                else
                {
                    CreateLinuxSymlink(wrapperPath, targetBinaryName);
                }
            }
            else
            {
                if (File.Exists(wrapperPath))
                {
                    File.Delete(wrapperPath);
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
            using var resourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (resourceStream == null)
            {
                throw new FileNotFoundException($"Embedded resource not found: {resourceName}.");
            }
            using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write);
            resourceStream.CopyTo(fileStream);
        }

        private void MakeExecutable(string path)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo { FileName = "/bin/chmod", Arguments = $"+x \"{path}\"" }
            };
            process.Start();
            process.WaitForExit();
        }

        private void CreateLinuxSymlink(string symlinkPath, string targetName)
        {
             if (File.Exists(symlinkPath))
            {
                File.Delete(symlinkPath);
            }
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/bin/ln",
                    Arguments = $"-sf \"{targetName}\" \"{symlinkPath}\"",
                    WorkingDirectory = Path.GetDirectoryName(symlinkPath)
                }
            };
            process.Start();
            process.WaitForExit();
        }
    }
}
