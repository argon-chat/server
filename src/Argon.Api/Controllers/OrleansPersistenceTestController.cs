namespace Argon.Api.Controllers;

using Grains.Interfaces;
using Microsoft.AspNetCore.Mvc;

#if DEBUG 
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
#endif