using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

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
        private readonly IMediaEncoder _mediaEncoder;
        private readonly IServerConfigurationManager _configurationManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IConfiguration _startupConfig;

        public GrpcFfmpegController(
            IMediaEncoder mediaEncoder,
            IServerConfigurationManager configurationManager,
            ILibraryManager libraryManager,
            IConfiguration startupConfig)
        {
            _mediaEncoder = mediaEncoder;
            _configurationManager = configurationManager;
            _libraryManager = libraryManager;
            _startupConfig = startupConfig;
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
                OverridingEnvironmentVariables = SetupCheck.OverridingEnvironmentVariables(),
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
        /// Tests the setup with the saved settings, without the local fallback: the connection,
        /// the ffmpeg version, and whether the workers share Jellyfin's directories.
        /// </summary>
        [HttpPost("Test")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<object>> Test()
        {
            var plugin = Plugin.Instance;
            if (plugin?.Prepare() is null)
            {
                return Problem("The client could not be deployed, see the server log.");
            }

            var check = new SetupCheck(plugin.DeployDirectory, _configurationManager, _libraryManager, _startupConfig);
            return await check.RunAsync().ConfigureAwait(false);
        }
    }
}
