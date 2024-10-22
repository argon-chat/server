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
    public async Task<ActionResult<Tuple<HelloGrainOutputDto, HelloGrainOutputDto>>> Get()
    {
        var list = await _grain.GetList();
        return new Tuple<HelloGrainOutputDto, HelloGrainOutputDto>(
            new HelloGrainOutputDto(list["hellos"].Count, list["hellos"]),
            new HelloGrainOutputDto(list["ints"].Count, list["ints"]));
    }

    [HttpPost]
    public async Task<ActionResult<string>> Post([FromBody] HelloGrainInputDto dto)
    {
        return await _grain.Create(dto.Who);
    }
}
#endif