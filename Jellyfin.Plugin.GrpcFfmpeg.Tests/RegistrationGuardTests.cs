using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.GrpcFfmpeg.Tests
{
    [Collection(StaticStateCollection.Name)]
    public class RegistrationGuardTests
    {
        [Fact]
        public void FailedRegistrationLeavesServicesUntouched()
        {
            var services = new ServiceCollection();
            services.AddSingleton(new Mock<IMediaEncoder>().Object);

            PluginServiceRegistrator.TryRegisterHardwareClasses(services, (_, registrations) =>
            {
                // Half-way through, as with a type a newer Jellyfin version no longer has
                registrations.AddSingleton<object>(new object());
                throw new TypeLoadException("Could not load type 'MediaBrowser.Controller.MediaEncoding.ITranscodeManager'");
            });

            Assert.Single(services);
            Assert.Equal("TypeLoadException: Could not load type 'MediaBrowser.Controller.MediaEncoding.ITranscodeManager'", PluginServiceRegistrator.HardwareClassesUnavailable);
        }

        [Fact]
        public void SuccessfulRegistrationAddsEverything()
        {
            var services = new ServiceCollection();

            PluginServiceRegistrator.TryRegisterHardwareClasses(services, (_, registrations) =>
            {
                registrations.AddSingleton<object>(new object());
                registrations.AddSingleton("text");
            });

            Assert.Equal(2, services.Count);
            Assert.Null(PluginServiceRegistrator.HardwareClassesUnavailable);
        }
    }
}
