using Jellyfin.Plugin.GrpcFfmpeg.Configuration;

namespace Jellyfin.Plugin.GrpcFfmpeg.HardwareClasses
{
    /// <summary>
    /// A streaming request of a playback session. Its hardware class is chosen when
    /// Jellyfin first reads the transcoding settings for it (inside the controller, after
    /// authentication), and then kept for the rest of the request.
    /// </summary>
    /// <remarks>
    /// The request flows into everything started while it is handled. That includes the
    /// ffmpeg process and the tasks following its output, which should see the class, but
    /// could also be a timer some service happens to create then. So once the request has
    /// ended, the class only applies while the session's transcode runs.
    /// </remarks>
    internal sealed class StreamRequest
    {
        private readonly object _lock = new();
        private readonly SessionClassSelector? _selector;
        private bool _resolved;
        private volatile bool _completed;
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
        /// Marks the request as handled.
        /// </summary>
        public void Complete() => _completed = true;

        /// <summary>
        /// Gets the session's class, choosing it on first use.
        /// </summary>
        public HardwareClassSettings? Resolve()
        {
            if (_completed)
            {
                return ResolveAfterRequest();
            }

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

        private HardwareClassSettings? ResolveAfterRequest()
        {
            HardwareClassSettings? hardwareClass;
            lock (_lock)
            {
                // Never chosen after the request
                hardwareClass = _class;
            }

            try
            {
                return hardwareClass is not null && _selector?.IsTranscoding(PlaySessionId) == true ? hardwareClass : null;
            }
            catch (Exception ex)
            {
                HardwareClassDiagnostics.Error("checking the session's transcode", ex);
                return null;
            }
        }
    }
}
