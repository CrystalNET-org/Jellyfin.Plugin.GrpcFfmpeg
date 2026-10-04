using Jellyfin.Plugin.GrpcFfmpeg.Configuration;
using Jellyfin.Plugin.GrpcFfmpeg.HardwareClasses;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.GrpcFfmpeg.Tests
{
    public class ConfigurationManagerProxyTests
    {
        private readonly EncodingOptions _global = new()
        {
            HardwareAccelerationType = HardwareAccelerationType.none,
            QsvDevice = "/dev/dri/renderD128",
            HardwareDecodingCodecs = new[] { "h264" },
            EncoderPreset = EncoderPreset.veryfast,
            AllowHevcEncoding = false,
        };

        private readonly Mock<IServerConfigurationManager> _inner = new();
        private HardwareClassSettings? _current;

        public ConfigurationManagerProxyTests()
        {
            _inner.Setup(m => m.GetConfiguration("encoding")).Returns(() => _global);
        }

        private IServerConfigurationManager CreateProxy() => ConfigurationManagerProxy.Create(_inner.Object, () => _current);

        private static HardwareClassSettings Nvidia() => new()
        {
            Name = HardwareClassSettings.Nvidia,
            Enabled = true,
            GrpcHost = "workers-nvidia",
            HardwareDecodingCodecs = new[] { "h264", "hevc", "av1" },
            AllowHevcEncoding = true,
            EnableTonemapping = true,
        };

        [Fact]
        public void ReturnsGlobalOptionsWithoutClass()
        {
            var proxy = CreateProxy();
            Assert.Same(_global, proxy.GetEncodingOptions());
            Assert.Same(_global, proxy.GetConfiguration<EncodingOptions>("encoding"));
        }

        [Fact]
        public void AppliesClassToCopy()
        {
            var proxy = CreateProxy();
            _current = Nvidia();

            var options = proxy.GetEncodingOptions();

            Assert.NotSame(_global, options);
            Assert.Equal(HardwareAccelerationType.nvenc, options.HardwareAccelerationType);
            Assert.Equal(new[] { "h264", "hevc", "av1" }, options.HardwareDecodingCodecs);
            Assert.True(options.AllowHevcEncoding);
            Assert.True(options.EnableTonemapping);
            Assert.False(options.EnableVppTonemapping);
            // Not overridden: taken from the global options
            Assert.Equal(EncoderPreset.veryfast, options.EncoderPreset);
            Assert.Equal("/dev/dri/renderD128", options.QsvDevice);

            // The global options are untouched
            Assert.Equal(HardwareAccelerationType.none, _global.HardwareAccelerationType);
            Assert.Equal(new[] { "h264" }, _global.HardwareDecodingCodecs);
            Assert.False(_global.AllowHevcEncoding);
        }

        [Fact]
        public void IntelClassSetsQsvDevice()
        {
            var proxy = CreateProxy();
            _current = new HardwareClassSettings { Name = HardwareClassSettings.Intel, Enabled = true, GrpcHost = "w", Device = "/dev/dri/renderD129", EnableTonemapping = true };

            var options = proxy.GetEncodingOptions();

            Assert.Equal(HardwareAccelerationType.qsv, options.HardwareAccelerationType);
            Assert.Equal("/dev/dri/renderD129", options.QsvDevice);
            Assert.True(options.EnableVppTonemapping);
        }

        [Fact]
        public void CopiesAreNotShared()
        {
            var proxy = CreateProxy();
            _current = Nvidia();

            var first = proxy.GetEncodingOptions();
            var second = proxy.GetEncodingOptions();
            Assert.NotSame(first, second);

            // Arrays not set by the class are copied as well
            first.AllowOnDemandMetadataBasedKeyframeExtractionForExtensions[0] = "changed";
            first.HardwareDecodingCodecs[0] = "changed";
            Assert.NotEqual("changed", second.AllowOnDemandMetadataBasedKeyframeExtractionForExtensions[0]);
            Assert.NotEqual("changed", _global.AllowOnDemandMetadataBasedKeyframeExtractionForExtensions[0]);
            Assert.Equal("h264", second.HardwareDecodingCodecs[0]);
            Assert.Equal("h264", _global.HardwareDecodingCodecs[0]);
        }

        [Fact]
        public void OnlyEncodingIsIntercepted()
        {
            var network = new object();
            _inner.Setup(m => m.GetConfiguration("network")).Returns(network);
            var proxy = CreateProxy();
            _current = Nvidia();

            Assert.Same(network, proxy.GetConfiguration("network"));
        }

        [Fact]
        public void ForwardsOtherMembers()
        {
            var serverConfig = new ServerConfiguration();
            var paths = new Mock<IServerApplicationPaths>().Object;
            _inner.SetupGet(m => m.Configuration).Returns(serverConfig);
            _inner.SetupGet(m => m.ApplicationPaths).Returns(paths);
            _inner.SetupGet(m => m.CommonApplicationPaths).Returns(paths);
            var proxy = CreateProxy();
            _current = Nvidia();

            Assert.Same(serverConfig, proxy.Configuration);
            Assert.Same(paths, proxy.ApplicationPaths);
            // A member of the base interface
            Assert.Same(paths, ((IConfigurationManager)proxy).CommonApplicationPaths);

            var saved = new EncodingOptions();
            proxy.SaveConfiguration("encoding", saved);
            _inner.Verify(m => m.SaveConfiguration("encoding", saved), Times.Once);

            EventHandler<EventArgs> handler = (_, _) => { };
            proxy.ConfigurationUpdated += handler;
            _inner.VerifyAdd(m => m.ConfigurationUpdated += handler, Times.Once);
        }

        [Fact]
        public void ForwardsExceptionsUnwrapped()
        {
            _inner.Setup(m => m.GetConfiguration("broken")).Throws(new InvalidOperationException("boom"));
            var proxy = CreateProxy();

            var ex = Assert.Throws<InvalidOperationException>(() => proxy.GetConfiguration("broken"));
            Assert.Equal("boom", ex.Message);
        }

        [Fact]
        public void RefusesToSaveClassCopy()
        {
            var proxy = CreateProxy();
            _current = Nvidia();

            var copy = proxy.GetEncodingOptions();
            proxy.SaveConfiguration("encoding", copy);

            _inner.Verify(m => m.SaveConfiguration(It.IsAny<string>(), It.IsAny<object>()), Times.Never);
        }

        [Fact]
        public void ClassIsReadPerCall()
        {
            var proxy = CreateProxy();
            _current = Nvidia();
            Assert.Equal(HardwareAccelerationType.nvenc, proxy.GetEncodingOptions().HardwareAccelerationType);
            _current = null;
            Assert.Same(_global, proxy.GetEncodingOptions());
        }

        [Fact]
        public async Task ContextFlowsPerAsyncFlow()
        {
            var config = new PluginConfiguration { Enabled = true, EnableHardwareClasses = true, DefaultHardwareClass = HardwareClassSettings.Alternate };
            config.IntelClass.Enabled = true;
            config.NvidiaClass.Enabled = true;
            var selector = new SessionClassSelector(() => config);
            var proxy = ConfigurationManagerProxy.Create(_inner.Object, () => HardwareClassContext.Current);

            async Task<HardwareAccelerationType> Request(string? sessionId)
            {
                HardwareClassContext.Request = sessionId is null ? null : new StreamRequest(sessionId, null, null, Array.Empty<string>(), selector);
                await Task.Yield();
                return await Task.Run(() => proxy.GetEncodingOptions().HardwareAccelerationType);
            }

            var results = await Task.WhenAll(Request("a"), Request("b"), Request(null));

            Assert.Equal(
                new[] { HardwareAccelerationType.nvenc, HardwareAccelerationType.qsv },
                results.Take(2).OrderBy(r => r.ToString()));
            Assert.Equal(HardwareAccelerationType.none, results[2]);
            // Not leaked into the caller's flow
            Assert.Null(HardwareClassContext.Request);
        }
    }
}
