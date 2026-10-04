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
        /// Workers whose GPU encodes the given codecs and decodes the given test clips (all
        /// by default), and whose ffmpeg can make the given clips (all by default); records
        /// the commands.
        /// </summary>
        private static ClassProbe.ClientRunner Workers(List<string[]> commands, bool reachable = true, params string[] codecs) =>
            Workers(commands, reachable, codecs, ClassProbe.DecodeTests.Select(t => t.Id).ToArray(), ClassProbe.DecodeTests.Select(t => t.Id).ToArray());

        private static ClassProbe.ClientRunner Workers(List<string[]> commands, bool reachable, string[] codecs, string[] decodes, string[] clips) =>
            (name, arguments, stdin) =>
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

                if (arguments.Contains("pipe:1"))
                {
                    // A test clip: its "content" names the test, so the decode can tell which arrived
                    var test = ClassProbe.DecodeTests.First(t => string.Join(' ', arguments).Contains(string.Join(' ', t.Encoder), StringComparison.Ordinal));
                    return Task.FromResult(clips.Contains(test.Id)
                        ? new CommandResult(0, string.Empty, string.Empty, System.Text.Encoding.ASCII.GetBytes(test.Id))
                        : new CommandResult(1, string.Empty, "Unknown encoder"));
                }

                if (arguments.Contains("pipe:0"))
                {
                    var clip = System.Text.Encoding.ASCII.GetString(stdin!);
                    return Task.FromResult(decodes.Contains(clip)
                        ? new CommandResult(0, string.Empty, string.Empty)
                        : new CommandResult(1, string.Empty, "Codec not supported"));
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
            Assert.True(result.DecodingTested);
            // Version, three encodes, a clip and a decode per decoding test
            Assert.Equal(4 + (2 * ClassProbe.DecodeTests.Length), commands.Count);
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
        public void DecodeTestsForceTheGpuDecoder()
        {
            var hevc10 = ClassProbe.DecodeTests.Single(t => t.Id == "hevc10");
            var clip = string.Join(' ', ClassProbe.ClipArguments(hevc10));
            Assert.Contains("-c:v libx265 -pix_fmt yuv420p10le", clip, StringComparison.Ordinal);
            Assert.EndsWith("-f matroska pipe:1", clip, StringComparison.Ordinal);

            var nvidia = string.Join(' ', ClassProbe.DecodeTestArguments(_config.NvidiaClass, hevc10));
            Assert.Contains("-init_hw_device cuda=cu:0 -c:v hevc_cuvid -f matroska -i pipe:0", nvidia, StringComparison.Ordinal);
            var intel = string.Join(' ', ClassProbe.DecodeTestArguments(_config.IntelClass, hevc10));
            Assert.Contains("-init_hw_device qsv=qs@va -hwaccel qsv -hwaccel_device qs -c:v hevc_qsv -f matroska -i pipe:0", intel, StringComparison.Ordinal);
            Assert.DoesNotContain("hwaccel_output_format", intel, StringComparison.Ordinal);
        }

        [Fact]
        public async Task DetectsTheDecodersAndAppliesThem()
        {
            // As saved: wrong, and with VC-1, which cannot be tested
            _config.IntelClass.HardwareDecodingCodecs = new[] { "h264", "vc1", "av1" };
            _config.IntelClass.EnableDecodingColorDepth10Hevc = false;
            _config.IntelClass.EnableDecodingColorDepth10Vp9 = true;
            var decodes = new[] { "h264", "hevc", "hevc10", "vp8", "vp9" };
            var result = await ClassProbe.RunAsync(
                _config.IntelClass,
                Workers(new List<string[]>(), true, new[] { "h264", "hevc" }, decodes, ClassProbe.DecodeTests.Select(t => t.Id).ToArray()));

            Assert.Equal(decodes, result.Decoders);
            Assert.True(ClassProbe.Apply(_config, result));
            Assert.Equal(new[] { "h264", "hevc", "vc1", "vp8", "vp9" }, _config.IntelClass.HardwareDecodingCodecs);
            Assert.True(_config.IntelClass.EnableDecodingColorDepth10Hevc);
            Assert.False(_config.IntelClass.EnableDecodingColorDepth10Vp9);
            Assert.False(ClassProbe.Apply(_config, result));
        }

        [Fact]
        public async Task KeepsWhatCouldNotBeTested()
        {
            // The workers' ffmpeg cannot make AV1 or VP8 clips: those settings stay
            _config.NvidiaClass.HardwareDecodingCodecs = new[] { "h264", "vp8" };
            var clips = new[] { "h264", "hevc", "hevc10", "mpeg2video", "vp9", "vp910" };
            var result = await ClassProbe.RunAsync(
                _config.NvidiaClass,
                Workers(new List<string[]>(), true, new[] { "h264" }, ClassProbe.DecodeTests.Select(t => t.Id).ToArray(), clips));

            Assert.Equal(clips, result.DecodeTested);
            Assert.True(ClassProbe.Apply(_config, result));
            Assert.Equal(new[] { "h264", "hevc", "mpeg2video", "vp8", "vp9" }, _config.NvidiaClass.HardwareDecodingCodecs);
        }

        [Fact]
        public async Task IgnoresADecodingTestThatDoesNotWork()
        {
            // Not even H.264 decoded: the test is broken (e.g. a decoder setup that does not fit
            // the driver), so it must not turn off hardware decoding
            var before = _config.NvidiaClass.HardwareDecodingCodecs.ToArray();
            var result = await ClassProbe.RunAsync(
                _config.NvidiaClass,
                Workers(new List<string[]>(), true, new[] { "h264", "hevc" }, Array.Empty<string>(), ClassProbe.DecodeTests.Select(t => t.Id).ToArray()));

            Assert.True(result.GpuUsable);
            Assert.False(result.DecodingTested);
            Assert.True(ClassProbe.Apply(_config, result));
            Assert.True(_config.NvidiaClass.AllowHevcEncoding);
            Assert.Equal(before, _config.NvidiaClass.HardwareDecodingCodecs);
        }

        [Fact]
        public void ToneMappingIsOnForNewClasses()
        {
            var config = new PluginConfiguration();
            Assert.All(config.HardwareClasses(), c => Assert.True(c.EnableTonemapping));
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
