using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Jellyfin.Plugin.GrpcFfmpeg.Configuration;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.GrpcFfmpeg.Managers
{
    public class DeploymentManager
    {
        public DeploymentManager() 
        {
        }

        public void Deploy(PluginConfiguration config)
        {
            if (string.IsNullOrWhiteSpace(config.DeployPath))
            {
                throw new ArgumentException("Deploy path cannot be empty.");
            }

            Directory.CreateDirectory(config.DeployPath);

            var (resourceName, binaryName) = GetPlatformAwareBinaryNames();
            var binaryPath = Path.Combine(config.DeployPath, binaryName);

            ExtractEmbeddedResource(resourceName, binaryPath);

            var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            var scriptName = isWindows ? "grpc-wrapper.bat" : "grpc-wrapper.sh";
            var scriptPath = Path.Combine(config.DeployPath, scriptName);
            
            var allEnvVars = new List<EnvVar>
            {
                new EnvVar { Key = "USE_SSL", Value = config.UseSsl.ToString().ToLower() },
                new EnvVar { Key = "GRPC_HOST", Value = config.GrpcHost },
                new EnvVar { Key = "GRPC_PORT", Value = config.GrpcPort.ToString() },
                new EnvVar { Key = "CERTIFICATE_PATH", Value = config.CertificatePath },
                new EnvVar { Key = "AUTH_TOKEN", Value = config.AuthToken }
            };

            // Removed logic for ExtraEnvVars

            if (isWindows)
            {
                GenerateWindowsWrapperScript(scriptPath, binaryName, allEnvVars);
            }
            else
            {
                MakeExecutable(binaryPath);
                GenerateUnixWrapperScript(scriptPath, binaryName, allEnvVars);
                MakeExecutable(scriptPath);
            }

            if (config.SymlinkFFmpeg)
            {
                var symlinkPath = Path.Combine(config.DeployPath, isWindows ? "ffmpeg.bat" : "ffmpeg");
                CreateSymlink(symlinkPath, scriptName, isWindows);
            }

            if (config.SymlinkFFprobe)
            {
                var symlinkPath = Path.Combine(config.DeployPath, isWindows ? "ffprobe.bat" : "ffprobe");
                CreateSymlink(symlinkPath, scriptName, isWindows);
            }
        }
        
        // This is now only used internally by this class
        private class EnvVar 
        {
            public string? Key { get; set; }
            public string? Value { get; set; }
        }

        // ... other methods are the same ...
        private void GenerateWindowsWrapperScript(string scriptPath, string binaryName, List<EnvVar> envVars)
        {
            var scriptContent = new StringBuilder();
            scriptContent.AppendLine("@echo off");
            foreach (var envVar in envVars.Where(ev => !string.IsNullOrEmpty(ev.Key)))
            {
                scriptContent.AppendLine($"set {envVar.Key}={envVar.Value}");
            }
            scriptContent.AppendLine($"{binaryName} %*");
            File.WriteAllText(scriptPath, scriptContent.ToString());
        }
        
        private (string resourceName, string binaryName) GetPlatformAwareBinaryNames()
        {
            string os, arch, ext;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
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
                throw new FileNotFoundException($"Embedded resource not found: {resourceName}. This can happen if the platform is not supported.");
            }
            using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write);
            resourceStream.CopyTo(fileStream);
        }

        private void GenerateUnixWrapperScript(string scriptPath, string binaryName, List<EnvVar> envVars)
        {
            var scriptContent = new StringBuilder();
            scriptContent.AppendLine("#!/bin/bash");
            foreach (var envVar in envVars.Where(ev => !string.IsNullOrEmpty(ev.Key)))
            {
                scriptContent.AppendLine($"export {envVar.Key}=\"{envVar.Value}\"");
            }
            scriptContent.AppendLine($"exec ./{binaryName} \"$@\"");
            File.WriteAllText(scriptPath, scriptContent.ToString());
        }

        private void MakeExecutable(string path)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
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
            }
        }

        private void CreateSymlink(string symlinkPath, string targetName, bool isWindows)
        {
            if (File.Exists(symlinkPath))
            {
                File.Delete(symlinkPath);
            }

            if (isWindows)
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c mklink \"{symlinkPath}\" \"{targetName}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = Path.GetDirectoryName(symlinkPath)
                    }
                };
                process.Start();
                process.WaitForExit();
            }
            else
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/bin/ln",
                        Arguments = $"-sf \"{targetName}\" \"{symlinkPath}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = Path.GetDirectoryName(symlinkPath)
                    }
                };
                process.Start();
                process.WaitForExit();
            }
        }
    }
}