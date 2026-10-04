using Jellyfin.Plugin.GrpcFfmpeg.Configuration;

namespace Jellyfin.Plugin.GrpcFfmpeg.HardwareClasses
{
    /// <summary>
    /// A streaming request of a playback session. Its hardware class is chosen when
    /// Jellyfin first reads the transcoding settings for it (inside the controller, after
    /// authentication), and then kept for the rest of the request.
    /// </summary>
    internal sealed class StreamRequest
    {
        private readonly object _lock = new();
        private readonly SessionClassSelector? _selector;
        private bool _resolved;
        private HardwareClassSettings? _class;

        public StreamRequest(
            string playSessionId,
            Guid? itemId,
            Guid? mediaSourceId,
            IReadOnlyList<string> requestedVideoCodecs,
            SessionClassSelector? selector)
        {
            PlaySessionId = playSessionId;
            ItemId = itemId;
            MediaSourceId = mediaSourceId;
            RequestedVideoCodecs = requestedVideoCodecs;
            _selector = selector;
        }

        public string PlaySessionId { get; }

        /// <summary>Gets the item from the URL (/Videos/{itemId}/…).</summary>
        public Guid? ItemId { get; }

        /// <summary>Gets the MediaSourceId query value, for items with several versions.</summary>
        public Guid? MediaSourceId { get; }

        /// <summary>Gets the VideoCodec query value: the codecs the client accepts, preferred first.</summary>
        public IReadOnlyList<string> RequestedVideoCodecs { get; }

        /// <summary>
        /// Gets the session's class, choosing it on first use.
        /// </summary>
        public HardwareClassSettings? Resolve()
        {
            lock (_lock)
            {
                if (!_resolved)
                {
                    HardwareClassDiagnostics.Resolved();
                    try
                    {
                        _class = _selector?.Select(this);
                    }
                    catch (Exception ex)
                    {
                        // Never break playback: this request uses Jellyfin's settings
                        HardwareClassDiagnostics.Error("choosing the class", ex);
                        _class = null;
                    }

                    _resolved = true;
                }

                return _class;
            }
        }
    }
}
