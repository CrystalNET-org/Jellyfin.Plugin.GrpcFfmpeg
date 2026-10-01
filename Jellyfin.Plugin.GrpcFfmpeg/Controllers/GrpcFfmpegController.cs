using System.Diagnostics;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.GrpcFfmpeg.Controllers
{
    /// <summary>
    /// Status and actions for the plugin's configuration page.
    /// </summary>
    [ApiController]
    [Route("GrpcFfmpeg")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public class GrpcFfmpegController : ControllerBase
    {
        private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);
        private readonly IMediaEncoder _mediaEncoder;

        public GrpcFfmpegController(IMediaEncoder mediaEncoder)
        {
            _mediaEncoder = mediaEncoder;
        }

        /// <summary>
        /// Gets the deployment state and whether Jellyfin currently uses the client.
        /// </summary>
        [HttpGet("Status")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<object> GetStatus()
        {
            var plugin = Plugin.Instance;
            if (plugin is null)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            var clientPath = Relay.FfmpegPath(plugin.DeployDirectory);
            var active = string.Equals(_mediaEncoder.EncoderPath, clientPath, StringComparison.Ordinal);
            return new
            {
                plugin.DeployDirectory,
                ClientPath = clientPath,
                PlatformSupported = Relay.ClientResourceName() is not null,
                Deployed = System.IO.File.Exists(clientPath),
                plugin.Configuration.Enabled,
                ActiveFfmpegPath = _mediaEncoder.EncoderPath,
                Active = active,
                RestartRequired = plugin.Configuration.Enabled != active,
                plugin.FallbackDirectory,
            };
        }

        /// <summary>
        /// Gets the clients' activity log lines after the given id.
        /// </summary>
        /// <param name="after">Id of the last line the caller already has.</param>
        [HttpGet("Console")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<object> GetConsole([FromQuery] long after = 0)
        {
            var (lastId, lines) = ActivityConsole.Read(after);
            return new { LastId = lastId, Lines = lines.Select(line => new { line.Id, line.Text }) };
        }

        /// <summary>
        /// Clears the activity log shown in the console.
        /// </summary>
        [HttpPost("Console/Clear")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult ClearConsole()
        {
            ActivityConsole.Clear();
            return NoContent();
        }

        /// <summary>
        /// Redeploys the client and its config file.
        /// </summary>
        [HttpPost("Deploy")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public ActionResult Deploy()
        {
            if (Plugin.Instance?.Prepare() is null)
            {
                return Problem("Deployment failed, see the server log.");
            }

            return NoContent();
        }

        /// <summary>
        /// Runs "ffmpeg -version" on a worker with the saved settings, without the local fallback.
        /// </summary>
        [HttpPost("Test")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<object>> Test()
        {
            var ffmpegPath = Plugin.Instance?.Prepare();
            if (ffmpegPath is null)
            {
                return Problem("The client could not be deployed, see the server log.");
            }

            var startInfo = new ProcessStartInfo(ffmpegPath)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-version");
            // Test the worker itself, not the fallback, and fail fast
            startInfo.Environment["FALLBACK_DIR"] = string.Empty;
            startInfo.Environment["RETRIES"] = "1";

            using var process = Process.Start(startInfo)!;
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(_testTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return new { Success = false, ExitCode = -1, Output = $"No answer within {_testTimeout.TotalSeconds} seconds" };
            }

            var output = process.ExitCode == 0 ? await stdout.ConfigureAwait(false) : await stderr.ConfigureAwait(false);
            return new
            {
                Success = process.ExitCode == 0,
                process.ExitCode,
                Output = output.Trim(),
            };
        }
    }
}
