using Jellyfin.Plugin.GrpcFfmpeg.Configuration;
using Jellyfin.Plugin.GrpcFfmpeg.HardwareClasses;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Plugin.GrpcFfmpeg.Tests
{
    [Collection(StaticStateCollection.Name)]
    public class SessionClassSelectorTests
    {
        private readonly PluginConfiguration _config = new()
        {
            Enabled = true,
            EnableHardwareClasses = true,
            DefaultHardwareClass = HardwareClassSettings.Alternate,
        };

        private readonly HashSet<string> _transcoding = new();
        private readonly HashSet<string> _unreachable = new();
        private readonly Dictionary<string, SourceVideo> _videos = new();
        private DateTime _now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

        public SessionClassSelectorTests()
        {
            _config.IntelClass.Enabled = true;
            _config.NvidiaClass.Enabled = true;
            // Only the NVIDIA class decodes AV1 in hardware here
            _config.IntelClass.HardwareDecodingCodecs = new[] { "h264", "hevc", "vp9" };
            _config.NvidiaClass.HardwareDecodingCodecs = new[] { "h264", "hevc", "vp9", "av1" };
        }

        private SessionClassSelector Create() => new(
            () => _config,
            request => _videos.TryGetValue(request.PlaySessionId, out var video) ? video : null,
            id => _transcoding.Contains(id),
            c => _unreachable.Contains(c.Name),
            () => _now);

        private static StreamRequest Request(string id, SessionClassSelector selector, params string[] codecs) =>
            new(id, Guid.NewGuid(), null, codecs, selector);

        private static string? Select(SessionClassSelector selector, string id, params string[] codecs) =>
            selector.Select(Request(id, selector, codecs))?.Name;

        [Fact]
        public void AlternatesBetweenClassesForNewSessions()
        {
            var selector = Create();
            Assert.Equal("intel", Select(selector, "a"));
            Assert.Equal("nvidia", Select(selector, "b"));
            Assert.Equal("intel", Select(selector, "c"));
        }

        [Fact]
        public void KeepsClassForSession()
        {
            var selector = Create();
            var first = Select(selector, "a");
            Select(selector, "b");
            for (var i = 0; i < 5; i++)
            {
                _now += TimeSpan.FromMinutes(30);
                Assert.Equal(first, Select(selector, "a"));
            }
        }

        [Fact]
        public void ReportsNewAssignmentsOnly()
        {
            var selector = Create();
            var assigned = new List<string>();
            selector.Assigned += (id, c, _) => assigned.Add(id + "=" + c?.Name);
            Select(selector, "a");
            Select(selector, "a");
            Select(selector, "b");
            Assert.Equal(new[] { "a=intel", "b=nvidia" }, assigned);
        }

        [Fact]
        public void ForgetsIdleSessions()
        {
            var selector = Create();
            Assert.Equal("intel", Select(selector, "a"));
            _now += SessionClassSelector.Expiry + TimeSpan.FromMinutes(10);
            // Expired: "a" is new again and takes the next turn
            Assert.Equal("nvidia", Select(selector, "a"));
            Assert.Equal(1, selector.Count);
        }

        [Fact]
        public void FixedDefaultClass()
        {
            _config.DefaultHardwareClass = "nvidia";
            var selector = Create();
            Assert.Equal("nvidia", Select(selector, "a"));
            Assert.Equal("nvidia", Select(selector, "b"));
        }

        [Fact]
        public void EmptyDefaultUsesJellyfinSettings()
        {
            _config.DefaultHardwareClass = string.Empty;
            Assert.Null(Select(Create(), "a"));
        }

        [Fact]
        public void SkipsUnusableClasses()
        {
            _config.NvidiaClass.GrpcHost = " ";
            var selector = Create();
            Assert.Equal("intel", Select(selector, "a"));
            Assert.Equal("intel", Select(selector, "b"));

            _config.DefaultHardwareClass = "nvidia";
            Assert.Null(Select(selector, "c"));
        }

        [Fact]
        public void DisabledMeansNoClass()
        {
            var selector = Create();
            Assert.NotNull(Select(selector, "a"));
            _config.EnableHardwareClasses = false;
            Assert.Null(Select(selector, "a"));
        }

        [Fact]
        public void ClassDisabledLaterIsReplaced()
        {
            _config.DefaultHardwareClass = HardwareClassSettings.Auto;
            var selector = Create();
            Assert.Equal("intel", Select(selector, "a"));
            _config.IntelClass.Enabled = false;
            Assert.Equal("nvidia", Select(selector, "a"));
        }

        [Fact]
        public void AutoPrefersClassThatDecodesTheMedia()
        {
            _config.DefaultHardwareClass = HardwareClassSettings.Auto;
            var selector = Create();
            _videos["av1"] = new SourceVideo("av1", 10);
            _videos["av1-2"] = new SourceVideo("av1", 8);
            Assert.Equal("nvidia", Select(selector, "av1"));
            // Even though NVIDIA is busier now
            _transcoding.Add("av1");
            Assert.Equal("nvidia", Select(selector, "av1-2"));
        }

        [Fact]
        public void AutoChecksBitDepth()
        {
            _config.DefaultHardwareClass = HardwareClassSettings.Auto;
            _config.IntelClass.EnableDecodingColorDepth10Hevc = false;
            var selector = Create();
            _videos["hevc10"] = new SourceVideo("hevc", 10);
            Assert.Equal("nvidia", Select(selector, "hevc10"));
            // 10-bit H.264: no class decodes it in hardware, so load decides
            _videos["h264-10"] = new SourceVideo("h264", 10);
            _transcoding.Add("hevc10");
            Assert.Equal("intel", Select(selector, "h264-10"));
        }

        [Fact]
        public void AutoPicksLeastLoadedClass()
        {
            _config.DefaultHardwareClass = HardwareClassSettings.Auto;
            var selector = Create();
            Assert.Equal("intel", Select(selector, "a"));
            // "a" is still starting, so it counts
            Assert.Equal("nvidia", Select(selector, "b"));

            _now += SessionClassSelector.StartingWindow + TimeSpan.FromSeconds(1);
            _transcoding.Add("a");
            // "b" stopped transcoding (e.g. direct play or ended): NVIDIA is idle
            Assert.Equal("nvidia", Select(selector, "c"));
            Assert.Equal(1, selector.Load("intel"));
            Assert.Equal(1, selector.Load("nvidia"));
        }

        [Fact]
        public void AutoUsesWeights()
        {
            _config.DefaultHardwareClass = HardwareClassSettings.Auto;
            _config.NvidiaClass.Weight = 3;
            var selector = Create();
            var picks = Enumerable.Range(0, 8).Select(i => Select(selector, "s" + i)).ToList();
            Assert.Equal(2, picks.Count(p => p == "intel"));
            Assert.Equal(6, picks.Count(p => p == "nvidia"));
        }

        [Fact]
        public void AutoPrefersClientsCodecWhenLoadIsEqual()
        {
            _config.DefaultHardwareClass = HardwareClassSettings.Auto;
            _config.NvidiaClass.AllowAv1Encoding = true;
            var selector = Create();
            Assert.Equal("nvidia", Select(selector, "a", "av1", "hevc", "h264"));
            // Load now differs: load wins over the codec preference
            Assert.Equal("intel", Select(selector, "b", "av1", "h264"));
        }

        [Fact]
        public void AutoAvoidsUnreachableClass()
        {
            _config.DefaultHardwareClass = HardwareClassSettings.Auto;
            _unreachable.Add("intel");
            var selector = Create();
            Assert.Equal("nvidia", Select(selector, "a"));
            Assert.Equal("nvidia", Select(selector, "b"));
            // All unreachable: still assigns one
            _unreachable.Add("nvidia");
            Assert.NotNull(Select(selector, "c"));
        }

        [Fact]
        public void MovesSessionOffDownClassWhenNothingRuns()
        {
            _config.DefaultHardwareClass = HardwareClassSettings.Auto;
            var selector = Create();
            Assert.Equal("intel", Select(selector, "a"));
            _transcoding.Add("a");
            _unreachable.Add("intel");
            // Still transcoding there: keep it
            Assert.Equal("intel", Select(selector, "a"));
            // Its transcode is gone (failed or stopped): move it
            _transcoding.Remove("a");
            Assert.Equal("nvidia", Select(selector, "a"));
        }

        [Fact]
        public void MediaLookupFailureDoesNotBreakSelection()
        {
            _config.DefaultHardwareClass = HardwareClassSettings.Auto;
            var selector = new SessionClassSelector(() => _config, _ => throw new InvalidOperationException("db"));
            Assert.Equal("intel", selector.Select(Request("a", selector))?.Name);
        }

        [Fact]
        public void RequestResolvesOnce()
        {
            var selector = Create();
            var request = Request("a", selector);
            Assert.Equal("intel", request.Resolve()?.Name);
            _config.IntelClass.Enabled = false;
            Assert.Equal("intel", request.Resolve()?.Name);
        }

        [Theory]
        [InlineData("/Videos/2c3cd9f4b7e24e5f9e7a6b9c1d2e3f40/main.m3u8", "?PlaySessionId=abc&VideoCodec=hevc,h264", "abc")]
        [InlineData("/videos/2c3cd9f4b7e24e5f9e7a6b9c1d2e3f40/hls1/main/12.mp4", "?playSessionId=abc", "abc")]
        [InlineData("/jellyfin/Videos/2c3cd9f4b7e24e5f9e7a6b9c1d2e3f40/stream.mkv", "?PlaySessionId=abc", "abc")]
        [InlineData("/Audio/2c3cd9f4b7e24e5f9e7a6b9c1d2e3f40/universal", "?PlaySessionId=abc", "abc")]
        [InlineData("/Videos/2c3cd9f4b7e24e5f9e7a6b9c1d2e3f40/main.m3u8", "?VideoCodec=h264", null)]
        [InlineData("/Items/2c3cd9f4b7e24e5f9e7a6b9c1d2e3f40/PlaybackInfo", "?PlaySessionId=abc", null)]
        [InlineData("/Sessions/Playing", "?PlaySessionId=abc", null)]
        [InlineData("/Videos", "?PlaySessionId=abc", null)]
        public void ParsesStreamingRequests(string path, string query, string? expected)
        {
            var queryCollection = new QueryCollection(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query));
            var request = HardwareClassStartupFilter.ParseStreamRequest(new PathString(path), queryCollection, null);
            Assert.Equal(expected, request?.PlaySessionId);
            if (request is not null)
            {
                Assert.Equal(Guid.Parse("2c3cd9f4b7e24e5f9e7a6b9c1d2e3f40"), request.ItemId);
            }
        }

        [Fact]
        public void ParsesCodecsAndMediaSource()
        {
            var query = new QueryCollection(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(
                "?PlaySessionId=abc&VideoCodec=av1, hevc,h264&MediaSourceId=0f0e0d0c0b0a09080706050403020100"));
            var request = HardwareClassStartupFilter.ParseStreamRequest(new PathString("/Videos/not-a-guid/main.m3u8"), query, null)!;
            Assert.Equal(new[] { "av1", "hevc", "h264" }, request.RequestedVideoCodecs);
            Assert.Equal(Guid.Parse("0f0e0d0c0b0a09080706050403020100"), request.MediaSourceId);
            Assert.Null(request.ItemId);
        }

        [Fact]
        public void MarkerPathMatchesClient()
        {
            // The client sanitizes "host_port": letters, digits, '.' and '-' stay, the rest becomes '_'
            Assert.Equal(Path.Combine(Path.GetTempPath(), "grpc-ffmpeg-unreachable-workers-nvidia.lan_50051"), ClassHealth.MarkerPath("workers-nvidia.lan", 50051));
            Assert.Equal(Path.Combine(Path.GetTempPath(), "grpc-ffmpeg-unreachable-fd00__1_50051"), ClassHealth.MarkerPath("[fd00::1]", 50051));
        }

        [Fact]
        public void ClassAddressesForClient()
        {
            _config.NvidiaClass.GrpcPort = 50052;
            Assert.Equal("intel=ffmpeg-workers-intel:50051;nvidia=ffmpeg-workers-nvidia:50052", Relay.ClassAddresses(_config, active: true));
            _config.NvidiaClass.GrpcHost = "fd00::1";
            Assert.Equal("intel=ffmpeg-workers-intel:50051;nvidia=[fd00::1]:50052", Relay.ClassAddresses(_config, active: true));
            _config.NvidiaClass.GrpcHost = "[fd00::1]";
            Assert.Equal("intel=ffmpeg-workers-intel:50051;nvidia=[fd00::1]:50052", Relay.ClassAddresses(_config, active: true));
        }

        [Fact]
        public void ClassAddressesFollowStartupNotTheSwitch()
        {
            // Switched on without a restart: Jellyfin does not use the classes yet
            Assert.Null(Relay.ClassAddresses(_config, active: false));
            // Switched off without a restart: sessions may still run with class settings
            _config.EnableHardwareClasses = false;
            Assert.NotNull(Relay.ClassAddresses(_config, active: true));
        }

        [Theory]
        [InlineData("workers:50051", "must not contain a port")]
        [InlineData("[fd00::1]:50051", "must not contain a port")]
        [InlineData("", "No host")]
        [InlineData("a b", "invalid characters")]
        [InlineData("a;b", "invalid characters")]
        public void RejectsInvalidClassHosts(string host, string error)
        {
            _config.IntelClass.GrpcHost = host;
            Assert.Contains(error, _config.IntelClass.AddressError, StringComparison.Ordinal);
            Assert.Null(_config.IntelClass.Address);
            Assert.Equal("nvidia=ffmpeg-workers-nvidia:50051", Relay.ClassAddresses(_config, active: true));
        }

        [Fact]
        public void ClassNamesComeFromTheirSetting()
        {
            // As read from an edited or old settings file
            _config.IntelClass.Name = string.Empty;
            _config.NvidiaClass.Name = "intel";
            _config.IntelClass = null!;
            Assert.Equal(new[] { "intel", "nvidia" }, _config.HardwareClasses().Select(c => c.Name));
            Assert.Equal("ffmpeg-workers-intel", _config.IntelClass.GrpcHost);
            Assert.Equal(MediaBrowser.Model.Entities.HardwareAccelerationType.nvenc, _config.NvidiaClass.AccelerationType);
        }

        [Fact]
        public void SessionOnJellyfinSettingsGetsClassOnceUsable()
        {
            _config.IntelClass.Enabled = false;
            _config.NvidiaClass.Enabled = false;
            var selector = Create();
            Assert.Null(Select(selector, "a"));
            _config.NvidiaClass.Enabled = true;
            // Not while its transcode runs
            _transcoding.Add("a");
            Assert.Null(Select(selector, "a"));
            _transcoding.Remove("a");
            Assert.Equal("nvidia", Select(selector, "a"));
            Assert.Equal("nvidia", Select(selector, "a"));
        }

        [Fact]
        public void ClassAppliesAfterTheRequestOnlyWhileTheSessionTranscodes()
        {
            var selector = Create();
            var request = Request("a", selector);
            Assert.Equal("intel", request.Resolve()?.Name);
            request.Complete();
            // e.g. a timer created during the request
            Assert.Null(request.Resolve());
            _transcoding.Add("a");
            // e.g. the tasks following the output of the ffmpeg process started for it
            Assert.Equal("intel", request.Resolve()?.Name);

            // Never chosen after the request
            var late = Request("b", selector);
            late.Complete();
            _transcoding.Add("b");
            Assert.Null(late.Resolve());
            Assert.Equal(1, selector.Count);
        }
    }
}
