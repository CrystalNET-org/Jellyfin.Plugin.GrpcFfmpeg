using Jellyfin.Plugin.GrpcFfmpeg.Configuration;
using Jellyfin.Plugin.GrpcFfmpeg.HardwareClasses;
using Xunit;

namespace Jellyfin.Plugin.GrpcFfmpeg.Tests
{
    /// <summary>
    /// Tests sharing the plugin's static state (diagnostics counters, the console) run one at a time.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public class StaticStateCollection
    {
        public const string Name = "Static state";
    }

    [Collection(StaticStateCollection.Name)]
    public sealed class HardwareClassDiagnosticsTests : IDisposable
    {
        private readonly PluginConfiguration _config = new() { Enabled = true, EnableHardwareClasses = true, DefaultHardwareClass = HardwareClassSettings.Auto };

        public HardwareClassDiagnosticsTests()
        {
            HardwareClassDiagnostics.Reset();
            HardwareClassContext.Active = true;
            _config.IntelClass.Enabled = true;
            _config.NvidiaClass.Enabled = true;
        }

        public void Dispose()
        {
            HardwareClassContext.Active = false;
            HardwareClassDiagnostics.Reset();
        }

        private static List<string> Warnings(Func<string, int>? transcoding = null) =>
            HardwareClassDiagnostics.Warnings(true, new[] { "intel", "nvidia" }, transcoding ?? (_ => 0));

        [Fact]
        public void NoWarningsWhenHealthy()
        {
            HardwareClassDiagnostics.StreamingRequest("/Videos/x/main.m3u8");
            HardwareClassDiagnostics.Resolved();
            HardwareClassDiagnostics.ClientRun("nvidia");
            Assert.Empty(Warnings(name => name == "nvidia" ? 3 : 0));
        }

        [Fact]
        public void WarnsWhenJellyfinReadsNoSettingsDuringHls()
        {
            HardwareClassDiagnostics.StreamingRequest("/Videos/x/main.m3u8");
            HardwareClassDiagnostics.StreamingRequest("/Videos/x/hls1/main/0.ts");
            Assert.Empty(Warnings());
            // Progressive streams do not count
            HardwareClassDiagnostics.StreamingRequest("/Videos/x/stream.mkv");
            Assert.Empty(Warnings());
            HardwareClassDiagnostics.StreamingRequest("/Videos/x/hls1/main/1.ts");
            Assert.Contains(Warnings(), w => w.Contains("read no transcoding settings", StringComparison.Ordinal));
        }

        [Fact]
        public void WarnsAboutOldClientFromConsoleLines()
        {
            ActivityConsole.AddServerLine("run: ffmpeg -version");
            Assert.Contains(Warnings(), w => w.Contains("ignored CLASS_ADDRESSES", StringComparison.Ordinal));
            // Not when no CLASS_ADDRESSES was written
            Assert.Empty(HardwareClassDiagnostics.Warnings(false, new[] { "intel" }, _ => 0));
        }

        [Fact]
        public void CountsRoutedCommandsFromConsoleLines()
        {
            ActivityConsole.AddServerLine("run [nvidia 10.0.0.1:50051]: ffmpeg -init_hw_device cuda=cu:0 -i a.mkv");
            ActivityConsole.AddServerLine("run [default ffmpeg-workers:50051]: ffmpeg -version");
            ActivityConsole.AddServerLine("exit 0 after 1.0s");
            Assert.Equal(1, HardwareClassDiagnostics.Routed("nvidia"));
            Assert.Equal(1, HardwareClassDiagnostics.Routed("default"));
            Assert.Equal(0, HardwareClassDiagnostics.UnroutedRuns);
        }

        [Fact]
        public void WarnsWhenClassTranscodesWithoutRoutedCommands()
        {
            HardwareClassDiagnostics.ClientRun("default");
            Assert.Empty(Warnings(name => name == "intel" ? 1 : 0));
            var warnings = Warnings(name => name == "intel" ? 2 : 0);
            Assert.Single(warnings);
            Assert.Contains("class intel", warnings[0], StringComparison.Ordinal);
        }

        [Fact]
        public void SelectionErrorsUseJellyfinSettingsAndWarn()
        {
            var now = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
            var selector = new SessionClassSelector(
                () => _config,
                isTranscoding: _ => throw new MissingMethodException("ITranscodeManager.GetTranscodingJob"),
                now: () => now);
            // The transcode manager is asked about the first session once it is past its starting window
            Assert.NotNull(new StreamRequest("a", null, null, Array.Empty<string>(), selector).Resolve());
            now += SessionClassSelector.StartingWindow + TimeSpan.FromSeconds(1);
            var request = new StreamRequest("b", null, null, Array.Empty<string>(), selector);

            Assert.Null(request.Resolve());
            Assert.Equal(1, HardwareClassDiagnostics.Errors);
            Assert.Equal(2, HardwareClassDiagnostics.Resolutions);
            Assert.Contains(Warnings(), w => w.Contains("MissingMethodException", StringComparison.Ordinal));
        }
    }
}
