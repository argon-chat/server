using Microsoft.AspNetCore.Mvc;

namespace Argon.Api.Controllers;
#if DEBUG
[Route("/orl/[controller]")]
public class OrleansPersistenceTestController : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        return Ok("OrleansPersistenceTestController");
    }
    
    [HttpPost]
    public IActionResult Post()
    {
        return Ok("OrleansPersistenceTestController");
    }
}
#endif