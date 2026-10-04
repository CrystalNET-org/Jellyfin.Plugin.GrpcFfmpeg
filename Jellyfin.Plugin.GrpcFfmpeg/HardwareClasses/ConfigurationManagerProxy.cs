using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Jellyfin.Plugin.GrpcFfmpeg.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.GrpcFfmpeg.HardwareClasses
{
    /// <summary>
    /// Wraps Jellyfin's configuration manager so that the encoding options read during a
    /// streaming request carry the settings of the session's hardware class.
    /// </summary>
    /// <remarks>
    /// A <see cref="DispatchProxy"/> rather than a class implementing the interface, so the
    /// plugin keeps loading when newer Jellyfin versions add members to it. Everything is
    /// forwarded; only GetConfiguration("encoding") (which GetEncodingOptions() and
    /// GetConfiguration&lt;EncodingOptions&gt; go through) is changed, and only while a class is
    /// set: it then returns a copy of the global options with the class's settings. The
    /// copy is never stored, and saving it is refused, so the global settings stay as they are.
    /// </remarks>
    public class ConfigurationManagerProxy : DispatchProxy
    {
        public const string EncodingKey = "encoding";

        private static readonly MethodInfo _memberwiseClone =
            typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

        // Copies handed out by this proxy, to refuse saving them
        private static readonly ConditionalWeakTable<object, object> _copies = new();

        private IServerConfigurationManager _inner = null!;
        private Func<HardwareClassSettings?> _currentClass = null!;
        private ILogger? _logger;

        /// <summary>
        /// Creates a proxy around <paramref name="inner"/>.
        /// </summary>
        /// <param name="inner">Jellyfin's configuration manager.</param>
        /// <param name="currentClass">Gets the class of the current request, if any.</param>
        /// <param name="logger">Logger, optional.</param>
        public static IServerConfigurationManager Create(
            IServerConfigurationManager inner,
            Func<HardwareClassSettings?> currentClass,
            ILogger? logger = null)
        {
            var proxy = Create<IServerConfigurationManager, ConfigurationManagerProxy>();
            var self = (ConfigurationManagerProxy)(object)proxy;
            self._inner = inner;
            self._currentClass = currentClass;
            self._logger = logger;
            return proxy;
        }

        /// <summary>
        /// Gets a copy of <paramref name="global"/> with the class's settings. Arrays are
        /// copied too, so changes to the copy never reach the global options.
        /// </summary>
        public static EncodingOptions WithClass(EncodingOptions global, HardwareClassSettings hardwareClass)
        {
            var copy = (EncodingOptions)_memberwiseClone.Invoke(global, null)!;
            foreach (var property in typeof(EncodingOptions).GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.PropertyType.IsArray && property.CanRead && property.CanWrite
                    && property.GetValue(copy) is Array array)
                {
                    property.SetValue(copy, array.Clone());
                }
            }

            copy.HardwareAccelerationType = hardwareClass.AccelerationType;
            if (hardwareClass.Name == HardwareClassSettings.Intel && !string.IsNullOrWhiteSpace(hardwareClass.Device))
            {
                copy.QsvDevice = hardwareClass.Device.Trim();
            }

            copy.HardwareDecodingCodecs = (string[])hardwareClass.HardwareDecodingCodecs.Clone();
            copy.EnableDecodingColorDepth10Hevc = hardwareClass.EnableDecodingColorDepth10Hevc;
            copy.EnableDecodingColorDepth10Vp9 = hardwareClass.EnableDecodingColorDepth10Vp9;
            copy.EnableHardwareEncoding = true;
            copy.EnableTonemapping = hardwareClass.EnableTonemapping;
            // Intel's VPP tonemapping is QSV-only; the OpenCL/CUDA one above covers both
            copy.EnableVppTonemapping = hardwareClass.Name == HardwareClassSettings.Intel && hardwareClass.EnableTonemapping;
            copy.AllowHevcEncoding = hardwareClass.AllowHevcEncoding;
            copy.AllowAv1Encoding = hardwareClass.AllowAv1Encoding;
            return copy;
        }

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);

            if (targetMethod.Name == "SaveConfiguration"
                && args is { Length: 2 }
                && args[1] is { } saved
                && _copies.TryGetValue(saved, out _))
            {
                // Would make one session's class settings Jellyfin's global ones
                _logger?.LogWarning("gRPC-ffmpeg: not saving the encoding options of a hardware class as Jellyfin's settings");
                return null;
            }

            object? result;
            try
            {
                result = targetMethod.Invoke(_inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Throw(ex.InnerException);
                throw;
            }

            if (targetMethod.Name == "GetConfiguration"
                && args is { Length: 1 }
                && args[0] is string key
                && string.Equals(key, EncodingKey, StringComparison.OrdinalIgnoreCase)
                && result is EncodingOptions global
                && _currentClass() is { } hardwareClass)
            {
                var copy = WithClass(global, hardwareClass);
                _copies.AddOrUpdate(copy, hardwareClass);
                return copy;
            }

            return result;
        }
    }
}
