using Jellyfin.Plugin.GrpcFfmpeg.Configuration;
using Jellyfin.Plugin.GrpcFfmpeg.HardwareClasses;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Plugin.GrpcFfmpeg.Tests
{
    public class SessionClassSelectorTests
    {
        private readonly PluginConfiguration _config = new()
        {
            Enabled = true,
            EnableHardwareClasses = true,
            DefaultHardwareClass = HardwareClassSettings.Alternate,
        };

        private DateTime _now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

        public SessionClassSelectorTests()
        {
            _config.IntelClass.Enabled = true;
            _config.NvidiaClass.Enabled = true;
        }

        private SessionClassSelector Create() => new(() => _config, () => _now);

        [Fact]
        public void AlternatesBetweenClassesForNewSessions()
        {
            var selector = Create();
            Assert.Equal("intel", selector.Select("a", out var isNew)?.Name);
            Assert.True(isNew);
            Assert.Equal("nvidia", selector.Select("b", out _)?.Name);
            Assert.Equal("intel", selector.Select("c", out _)?.Name);
        }

        [Fact]
        public void KeepsClassForSession()
        {
            var selector = Create();
            var first = selector.Select("a", out _)?.Name;
            selector.Select("b", out _);
            for (var i = 0; i < 5; i++)
            {
                _now += TimeSpan.FromMinutes(30);
                Assert.Equal(first, selector.Select("a", out var isNew)?.Name);
                Assert.False(isNew);
            }
        }

        [Fact]
        public void ForgetsIdleSessions()
        {
            var selector = Create();
            Assert.Equal("intel", selector.Select("a", out _)?.Name);
            _now += SessionClassSelector.Expiry + TimeSpan.FromMinutes(10);
            // Expired: "a" is new again and takes the next turn
            Assert.Equal("nvidia", selector.Select("a", out var isNew)?.Name);
            Assert.True(isNew);
            Assert.Equal(1, selector.Count);
        }

        [Fact]
        public void FixedDefaultClass()
        {
            _config.DefaultHardwareClass = "nvidia";
            var selector = Create();
            Assert.Equal("nvidia", selector.Select("a", out _)?.Name);
            Assert.Equal("nvidia", selector.Select("b", out _)?.Name);
        }

        [Fact]
        public void EmptyDefaultUsesJellyfinSettings()
        {
            _config.DefaultHardwareClass = string.Empty;
            Assert.Null(Create().Select("a", out _));
        }

        [Fact]
        public void SkipsUnusableClasses()
        {
            _config.NvidiaClass.GrpcHost = " ";
            var selector = Create();
            Assert.Equal("intel", selector.Select("a", out _)?.Name);
            Assert.Equal("intel", selector.Select("b", out _)?.Name);

            _config.DefaultHardwareClass = "nvidia";
            Assert.Null(selector.Select("c", out _));
        }

        [Fact]
        public void DisabledMeansNoClass()
        {
            var selector = Create();
            Assert.NotNull(selector.Select("a", out _));
            _config.EnableHardwareClasses = false;
            Assert.Null(selector.Select("a", out _));
        }

        [Fact]
        public void ClassDisabledLaterFallsBackToJellyfinSettings()
        {
            _config.DefaultHardwareClass = "nvidia";
            var selector = Create();
            Assert.Equal("nvidia", selector.Select("a", out _)?.Name);
            _config.NvidiaClass.Enabled = false;
            Assert.Null(selector.Select("a", out _));
        }

        [Theory]
        [InlineData("/Videos/1234/main.m3u8", "?PlaySessionId=abc&VideoCodec=h264", "abc")]
        [InlineData("/videos/1234/hls1/main/12.mp4", "?playSessionId=abc", "abc")]
        [InlineData("/jellyfin/Videos/1234/stream.mkv", "?PlaySessionId=abc", "abc")]
        [InlineData("/Audio/1234/universal", "?PlaySessionId=abc", "abc")]
        [InlineData("/Videos/1234/main.m3u8", "?VideoCodec=h264", null)]
        [InlineData("/Items/1234/PlaybackInfo", "?PlaySessionId=abc", null)]
        [InlineData("/Sessions/Playing", "?PlaySessionId=abc", null)]
        public void FindsStreamingSessionId(string path, string query, string? expected)
        {
            var queryCollection = new QueryCollection(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query));
            Assert.Equal(expected, HardwareClassStartupFilter.StreamingSessionId(new PathString(path), queryCollection));
        }

        [Fact]
        public void ClassAddressesForClient()
        {
            _config.NvidiaClass.GrpcPort = 50052;
            Assert.Equal("intel=ffmpeg-workers-intel:50051;nvidia=ffmpeg-workers-nvidia:50052", Relay.ClassAddresses(_config));
            _config.NvidiaClass.GrpcHost = "fd00::1";
            Assert.Equal("intel=ffmpeg-workers-intel:50051;nvidia=[fd00::1]:50052", Relay.ClassAddresses(_config));
            _config.EnableHardwareClasses = false;
            Assert.Null(Relay.ClassAddresses(_config));
        }
    }
}
