using Jellyfin.Plugin.GrpcFfmpeg.Configuration;
using Jellyfin.Plugin.GrpcFfmpeg.HardwareClasses;
using Xunit;

namespace Jellyfin.Plugin.GrpcFfmpeg.Tests
{
    [Collection(StaticStateCollection.Name)]
    public sealed class ClassProbeTests : IDisposable
    {
        private readonly PluginConfiguration _config = new() { EnableHardwareClasses = true };

        public ClassProbeTests()
        {
            ClassProbe.Reset();
            _config.IntelClass.Enabled = true;
            _config.NvidiaClass.Enabled = true;
        }

        public void Dispose() => ClassProbe.Reset();

        /// <summary>
        /// Workers whose GPU encodes the given codecs; records the commands.
        /// </summary>
        private static ClassProbe.ClientRunner Workers(List<string[]> commands, bool reachable = true, params string[] codecs) =>
            (name, arguments) =>
            {
                commands.Add(arguments.ToArray());
                if (!reachable)
                {
                    return Task.FromResult(new CommandResult(1, string.Empty, "gRPC error after 1 attempts: Unavailable: tcp connect error"));
                }

                if (arguments.Contains("-version"))
                {
                    return Task.FromResult(new CommandResult(0, "ffmpeg version 8.1.3-Jellyfin\nbuilt with gcc", string.Empty));
                }

                var encoder = arguments[arguments.ToList().IndexOf("-c:v") + 1];
                return Task.FromResult(codecs.Contains(encoder.Split('_')[0])
                    ? new CommandResult(0, string.Empty, string.Empty)
                    : new CommandResult(1, string.Empty, "[" + encoder + " @ 0x1] OpenEncodeSessionEx failed: unsupported device (2)"));
            };

        [Fact]
        public void NvencTestEncodeLikeJellyfin()
        {
            var arguments = string.Join(' ', ClassProbe.EncodeTestArguments(_config.NvidiaClass, "av1"));
            Assert.Contains("-init_hw_device cuda=cu:0", arguments, StringComparison.Ordinal);
            Assert.Contains("-f lavfi -i color=", arguments, StringComparison.Ordinal);
            Assert.Contains("-c:v av1_nvenc", arguments, StringComparison.Ordinal);
            Assert.EndsWith("-f null -", arguments, StringComparison.Ordinal);
        }

        [Fact]
        public void QsvTestEncodeLikeJellyfin()
        {
            var arguments = string.Join(' ', ClassProbe.EncodeTestArguments(_config.IntelClass, "hevc"));
            Assert.Contains("-init_hw_device vaapi=va:,vendor_id=0x8086,driver=iHD -init_hw_device qsv=qs@va -filter_hw_device qs", arguments, StringComparison.Ordinal);
            Assert.Contains("hwupload", arguments, StringComparison.Ordinal);
            Assert.Contains("-c:v hevc_qsv", arguments, StringComparison.Ordinal);

            _config.IntelClass.Device = "/dev/dri/renderD129";
            Assert.Contains("vaapi=va:/dev/dri/renderD129,driver=iHD", string.Join(' ', ClassProbe.EncodeTestArguments(_config.IntelClass, "h264")), StringComparison.Ordinal);
        }

        [Fact]
        public async Task DetectsTheEncodersAndAppliesThem()
        {
            _config.NvidiaClass.AllowHevcEncoding = false;
            _config.NvidiaClass.AllowAv1Encoding = false;
            var commands = new List<string[]>();
            var result = await ClassProbe.RunAsync(_config.NvidiaClass, Workers(commands, true, "h264", "hevc", "av1"));

            Assert.True(result.Reachable);
            Assert.True(result.GpuUsable);
            Assert.Equal("ffmpeg version 8.1.3-Jellyfin", result.Version);
            Assert.Equal(new[] { "h264", "hevc", "av1" }, result.Encoders);
            Assert.Equal(4, commands.Count);
            Assert.Same(result, ClassProbe.Latest("nvidia"));

            Assert.True(ClassProbe.Apply(_config, result));
            Assert.True(_config.NvidiaClass.AllowHevcEncoding);
            Assert.True(_config.NvidiaClass.AllowAv1Encoding);
            Assert.False(ClassProbe.Apply(_config, result));
        }

        [Fact]
        public async Task TurnsOffWhatTheGpuCannotEncode()
        {
            _config.IntelClass.AllowAv1Encoding = true;
            var result = await ClassProbe.RunAsync(_config.IntelClass, Workers(new List<string[]>(), true, "h264", "hevc"));
            Assert.Equal(new[] { "h264", "hevc" }, result.Encoders);
            Assert.Null(result.Error);
            Assert.True(ClassProbe.Apply(_config, result));
            Assert.True(_config.IntelClass.AllowHevcEncoding);
            Assert.False(_config.IntelClass.AllowAv1Encoding);
        }

        [Fact]
        public async Task KeepsTheSettingsWhenTheWorkersOrGpuFail()
        {
            _config.NvidiaClass.AllowHevcEncoding = true;
            var commands = new List<string[]>();
            var unreachable = await ClassProbe.RunAsync(_config.NvidiaClass, Workers(commands, false));
            Assert.False(unreachable.Reachable);
            Assert.Contains("Unavailable", unreachable.Error, StringComparison.Ordinal);
            Assert.Single(commands);
            Assert.False(ClassProbe.Apply(_config, unreachable));

            // No working GPU: stops after H.264, as the rest would fail the same way
            commands.Clear();
            var noGpu = await ClassProbe.RunAsync(_config.NvidiaClass, Workers(commands, true));
            Assert.True(noGpu.Reachable);
            Assert.False(noGpu.GpuUsable);
            Assert.Contains("could not encode H.264", noGpu.Error, StringComparison.Ordinal);
            Assert.Equal(2, commands.Count);
            Assert.False(ClassProbe.Apply(_config, noGpu));
            Assert.True(_config.NvidiaClass.AllowHevcEncoding);
        }

        [Fact]
        public async Task OnlyAppliesToTheWorkersThatWereChecked()
        {
            var result = await ClassProbe.RunAsync(_config.NvidiaClass, Workers(new List<string[]>(), true, "h264", "hevc", "av1"));
            // Saved meanwhile with other workers
            _config.NvidiaClass.GrpcHost = "other-workers";
            Assert.False(ClassProbe.Apply(_config, result));
            Assert.False(_config.NvidiaClass.AllowAv1Encoding);
        }

        [Fact]
        public void ClassChecksGoOnlyToTheClassWorkersAndStayOutOfTheConsole()
        {
            _config.NvidiaClass.GrpcHost = "fd00::1";
            var environment = SetupCheck.ClassEnvironment(_config.NvidiaClass);
            Assert.Equal("fd00::1", environment["GRPC_HOST"]);
            Assert.Equal("50051", environment["GRPC_PORT"]);
            // Empty values override the client's config file
            Assert.Equal(string.Empty, environment["CLASS_ADDRESSES"]);
            Assert.Equal(string.Empty, environment["LOG_FILE"]);
        }
    }
}
