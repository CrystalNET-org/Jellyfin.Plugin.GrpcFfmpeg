using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.GrpcFfmpeg.HardwareClasses
{
    /// <summary>
    /// Adds a middleware in front of Jellyfin's that sets the hardware class of the
    /// playback session for streaming requests (/Videos/…, /Audio/… with a PlaySessionId:
    /// streams, HLS playlists and segments).
    /// </summary>
    internal sealed class HardwareClassStartupFilter : IStartupFilter
    {
        private readonly SessionClassSelector _selector;
        private readonly ILogger<HardwareClassStartupFilter> _logger;

        public HardwareClassStartupFilter(SessionClassSelector selector, ILogger<HardwareClassStartupFilter> logger)
        {
            _selector = selector;
            _logger = logger;
        }

        /// <summary>
        /// Gets the PlaySessionId of a streaming request, or null for other requests.
        /// </summary>
        public static string? StreamingSessionId(PathString path, IQueryCollection query)
        {
            var value = path.Value;
            // Before UsePathBase, so a base URL may still be in front
            if (value is null
                || (value.IndexOf("/videos/", StringComparison.OrdinalIgnoreCase) < 0
                    && value.IndexOf("/audio/", StringComparison.OrdinalIgnoreCase) < 0))
            {
                return null;
            }

            // Query keys are case-insensitive
            var id = query["PlaySessionId"].ToString();
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }

        /// <inheritdoc />
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    var sessionId = StreamingSessionId(context.Request.Path, context.Request.Query);
                    if (sessionId is not null)
                    {
                        var hardwareClass = _selector.Select(sessionId, out var isNew);
                        if (isNew)
                        {
                            var name = hardwareClass?.Name ?? "default (Jellyfin's settings)";
                            _logger.LogInformation("gRPC-ffmpeg: playback session {PlaySessionId} uses hardware class {Class}", sessionId, name);
                            ActivityConsole.AddServerLine(string.Format(
                                CultureInfo.InvariantCulture,
                                "session {0} uses class {1}{2}",
                                sessionId,
                                name,
                                hardwareClass is null ? string.Empty : " (" + hardwareClass.AccelerationType + ", " + hardwareClass.Address + ")"));
                        }

                        HardwareClassContext.Current = hardwareClass;
                    }

                    await nextMiddleware().ConfigureAwait(false);
                });
                next(app);
            };
        }
    }
}
