using Microsoft.AspNetCore.Mvc;

namespace StealthDesk.web.server.Api.Internal;

[Route("api/internal/version")]
[ApiController]
public class VersionController : ControllerBase
{
    [HttpGet("server")]
    public ActionResult<Version> GetServerVersion()
    {
        var version = typeof(VersionController).Assembly.GetName().Version;

        if(version is null)
        {
            return NotFound();
        } 

        return Ok(version);
    }
}