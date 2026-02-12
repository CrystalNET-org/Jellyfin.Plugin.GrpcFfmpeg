using System;
using Jellyfin.Plugin.GrpcFfmpeg.Managers;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.GrpcFfmpeg.Controllers
{
    [ApiController]
    [Route("GrpcFfmpeg")]
    public class GrpcBridgeController : ControllerBase
    {
        public GrpcBridgeController()
        {
        }
        
        [HttpGet("DeployPath")]
        public ActionResult<object> GetDeployPath()
        {
             if (Plugin.Instance == null)
            {
                return StatusCode(503, "Plugin not initialized.");
            }
            return new { Path = Plugin.Instance.DeployPath };
        }

        [HttpGet("JellyfinFfmpegPath")] // New endpoint
        public ActionResult<object> GetJellyfinFfmpegPath()
        {
            if (Plugin.Instance == null)
            {
                return StatusCode(503, "Plugin not initialized.");
            }
            // Access the injected IMediaEncoder from the Plugin instance
            return new { Path = Plugin.Instance.MediaEncoder.EncoderPath };
        }

        [HttpPost("Deploy")]
        public IActionResult Deploy()
        {
            try
            {
                if (Plugin.Instance == null)
                {
                    return StatusCode(503, "Plugin not initialized.");
                }
                var config = Plugin.Instance.Configuration;
                Plugin.Instance.DeploymentManager.Deploy(config);
                return Ok();
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message, stackTrace = ex.StackTrace });
            }
        }
    }
}