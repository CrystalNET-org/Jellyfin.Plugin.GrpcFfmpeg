using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.GrpcFfmpeg.HardwareClasses
{
    /// <summary>
    /// Adds a middleware in front of Jellyfin's that marks streaming requests (/Videos/…,
    /// /Audio/… with a PlaySessionId: streams, HLS playlists and segments) as belonging to
    /// their playback session. The session's class is chosen later, when Jellyfin reads the
    /// transcoding settings, so only authenticated requests get that far.
    /// </summary>
    internal sealed class HardwareClassStartupFilter : IStartupFilter
    {
        private readonly SessionClassSelector _selector;

        public HardwareClassStartupFilter(SessionClassSelector selector, ILogger<HardwareClassStartupFilter> logger)
        {
            _selector = selector;
            _selector.Assigned += (sessionId, hardwareClass, reason) =>
            {
                var name = hardwareClass?.Name ?? "default (Jellyfin's settings)";
                logger.LogInformation("gRPC-ffmpeg: playback session {PlaySessionId} uses hardware class {Class} ({Reason})", sessionId, name, reason);
                ActivityConsole.AddServerLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "session {0} uses class {1}{2}: {3}",
                    sessionId,
                    name,
                    hardwareClass is null ? string.Empty : " (" + hardwareClass.AccelerationType + ", " + hardwareClass.Address + ")",
                    reason));
            };
        }

        /// <summary>
        /// Gets the streaming request, or null for other requests.
        /// </summary>
        public static StreamRequest? ParseStreamRequest(PathString path, IQueryCollection query, SessionClassSelector? selector)
        {
            // Query keys are case-insensitive
            var sessionId = query["PlaySessionId"].ToString();
            if (string.IsNullOrWhiteSpace(sessionId) || path.Value is not { } value)
            {
                return null;
            }

            // Before UsePathBase, so a base URL may still be in front: …/Videos/{itemId}/…
            var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var index = Array.FindIndex(segments, s =>
                s.Equals("videos", StringComparison.OrdinalIgnoreCase) || s.Equals("audio", StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index + 1 >= segments.Length)
            {
                return null;
            }

            var codecs = query["VideoCodec"].ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new StreamRequest(
                sessionId,
                Guid.TryParse(segments[index + 1], out var itemId) ? itemId : null,
                Guid.TryParse(query["MediaSourceId"].ToString(), out var mediaSourceId) ? mediaSourceId : null,
                codecs,
                selector);
        }

        /// <inheritdoc />
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    var request = ParseStreamRequest(context.Request.Path, context.Request.Query, _selector);
                    if (request is not null)
                    {
                        HardwareClassContext.Request = request;
                    }

                    await nextMiddleware().ConfigureAwait(false);
                });
                next(app);
            };
        }
    }
}
