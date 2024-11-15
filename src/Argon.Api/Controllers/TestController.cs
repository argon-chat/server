namespace Argon.Api.Controllers;

using Grains.Interfaces;
using Microsoft.AspNetCore.Mvc;

#if DEBUG
[ApiController, Route("api/[controller]")]
public class TestController(IGrainFactory grainFactory) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create()
    {
        var id     = Guid.NewGuid();
        var grain  = grainFactory.GetGrain<ITestGrain>(id);
        var input  = new SomeInput(1, "a");
        var result = await grain.CreateSomeInput(input);
        return Ok(new Tuple<Guid, SomeInput>(id, result));
    }

    [HttpPut]
    public async Task<IActionResult> Update(Guid id)
    {
        var grain  = grainFactory.GetGrain<ITestGrain>(id);
        var input  = new SomeInput(12, "a12");
        var result = await grain.UpdateSomeInput(input);
        return Ok(new Tuple<Guid, SomeInput>(id, result));
    }

    [HttpDelete]
    public async Task<IActionResult> Delete(Guid id)
    {
        var grain  = grainFactory.GetGrain<ITestGrain>(id);
        var result = await grain.DeleteSomeInput();
        return Ok(new Tuple<Guid, SomeInput>(id, result));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var grain  = grainFactory.GetGrain<ITestGrain>(id);
        var result = await grain.GetSomeInput();
        return Ok(new Tuple<Guid, SomeInput>(id, result));
    }


    [HttpPost("produce")]
    public async Task<IActionResult> Produce()
    {
        var grain = grainFactory.GetGrain<IStreamProducerGrain>(Guid.Empty);
        await grain.Produce();
        return Ok();
    }

    [HttpPost("consume")]
    public async Task<IActionResult> Consume()
    {
        var grain = grainFactory.GetGrain<IStreamConsumerGrain>(Guid.Empty);
        await grain.Consume();
        return Ok();
    }
}
#endif