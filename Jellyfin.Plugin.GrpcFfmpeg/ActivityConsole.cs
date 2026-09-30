using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.GrpcFfmpeg
{
    /// <summary>
    /// Collects the grpc-ffmpeg clients' activity log (see the client's LOG_FILE)
    /// in memory for the console on the settings page.
    /// </summary>
    /// <remarks>
    /// On Linux the clients write to a named pipe that is read here, so the log
    /// never touches the disk; while Jellyfin is not reading it, clients drop
    /// their lines without blocking. Elsewhere (Windows) the clients append to a
    /// file (rotated at 1 MB by the client) whose new lines are read here.
    /// </remarks>
    internal static class ActivityConsole
    {
        public const int Capacity = 1000;

        private static readonly object _lock = new();
        private static readonly Queue<ConsoleLine> _lines = new();
        private static long _lastId;
        private static string? _source;

        /// <summary>
        /// Gets the path the clients write their activity log to.
        /// </summary>
        public static string LogTarget(string deployDirectory) =>
            Path.Combine(deployDirectory, OperatingSystem.IsWindows() ? "grpc-ffmpeg.log" : "console.fifo");

        /// <summary>
        /// Starts collecting the log, once per process.
        /// </summary>
        public static void Start(string deployDirectory, ILogger logger)
        {
            var source = LogTarget(deployDirectory);
            lock (_lock)
            {
                if (_source is not null)
                {
                    return;
                }

                _source = source;
            }

            if (!OperatingSystem.IsWindows())
            {
                // Before the clients are told to use it, or they would create a regular file
                try
                {
                    EnsurePipe(source);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "gRPC-ffmpeg: could not create the activity log pipe {Path}", source);
                }
            }

            ThreadStart loop = OperatingSystem.IsWindows()
                ? () => TailFile(source, logger)
                : () => ReadPipe(source, logger);
            new Thread(loop) { IsBackground = true, Name = "gRPC-ffmpeg console" }.Start();
        }

        /// <summary>
        /// Gets the lines after <paramref name="afterId"/>, oldest first.
        /// </summary>
        public static (long LastId, List<ConsoleLine> Lines) Read(long afterId)
        {
            lock (_lock)
            {
                return (_lastId, _lines.Where(line => line.Id > afterId).ToList());
            }
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _lines.Clear();
            }
        }

        private static void Add(string text)
        {
            lock (_lock)
            {
                _lines.Enqueue(new ConsoleLine(++_lastId, text));
                while (_lines.Count > Capacity)
                {
                    _lines.Dequeue();
                }
            }
        }

        private static void ReadPipe(string path, ILogger logger)
        {
            var retry = false;
            while (true)
            {
                try
                {
                    if (retry)
                    {
                        EnsurePipe(path);
                    }

                    retry = true;
                    // Opened for writing too, so the pipe never reports end of
                    // file when the last client closes it
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, bufferSize: 1);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    string? line;
                    while ((line = reader.ReadLine()) is not null)
                    {
                        Add(line);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "gRPC-ffmpeg: reading the activity log {Path} failed, retrying", path);
                }

                Thread.Sleep(TimeSpan.FromSeconds(10));
            }
        }

        /// <summary>
        /// (Re)creates the named pipe; whatever was at the path before is replaced.
        /// </summary>
        private static void EnsurePipe(string path)
        {
            File.Delete(path);
            if (mkfifo(path, 0b110_000_000) != 0)
            {
                throw new IOException($"mkfifo {path} failed with errno {Marshal.GetLastPInvokeError()}");
            }
        }

        private static void TailFile(string path, ILogger logger)
        {
            long position = File.Exists(path) ? new FileInfo(path).Length : 0;
            var pending = new StringBuilder();
            while (true)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        var length = new FileInfo(path).Length;
                        if (length < position)
                        {
                            // Rotated by the client
                            position = 0;
                        }

                        if (length > position)
                        {
                            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            stream.Seek(position, SeekOrigin.Begin);
                            using var reader = new StreamReader(stream, Encoding.UTF8);
                            pending.Append(reader.ReadToEnd());
                            position = length;
                            var text = pending.ToString();
                            var end = text.LastIndexOf('\n');
                            if (end >= 0)
                            {
                                foreach (var line in text[..end].Split('\n'))
                                {
                                    Add(line.TrimEnd('\r'));
                                }

                                pending.Clear().Append(text[(end + 1)..]);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "gRPC-ffmpeg: reading the activity log {Path} failed", path);
                }

                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int mkfifo(string pathname, uint mode);
    }

    /// <summary>
    /// A line of the activity log.
    /// </summary>
    /// <param name="Id">Increasing number, for fetching only new lines.</param>
    /// <param name="Text">The line.</param>
    internal sealed record ConsoleLine(long Id, string Text);
}
