using Argon.Grains.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Argon.Api.Controllers;
// #if DEBUG // TODO: commented out to have this endpoint on staging for one version

public record HelloGrainInputDto(string Who);

public record HelloGrainOutputDto(long Count, IEnumerable<string> Whos);

[Route("/orl/[controller]")]
public class OrleansPersistenceTestController(IGrainFactory grainFactory) : ControllerBase
{
    private readonly IHello _grain = grainFactory.GetGrain<IHello>(0);

    [HttpGet]
    public async Task<ActionResult<HelloGrainOutputDto>> Get()
    {
        var list = await _grain.GetList();
        return new HelloGrainOutputDto(list.Count, list);
    }

    [HttpPost]
    public async Task<ActionResult<string>> Post([FromBody] HelloGrainInputDto dto)
    {
        return await _grain.Create(dto.Who);
    }
}
// #endif