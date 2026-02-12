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
        private readonly DeploymentManager _deploymentManager;

        public GrpcBridgeController(IApplicationPaths appPaths)
        {
            _deploymentManager = new DeploymentManager(appPaths);
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
                _deploymentManager.Deploy(config);
                return Ok();
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message, stackTrace = ex.StackTrace });
            }
        }
    }
}
