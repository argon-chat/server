namespace Argon.Api.Controllers;

using Grains.Interfaces;
using Microsoft.AspNetCore.Mvc;

[ApiController, Route("api/[controller]")]
public class TestController(IGrainFactory grainFactory) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create()
    {
        var grain  = grainFactory.GetGrain<ITestGrain>(Guid.NewGuid());
        var input  = new SomeInput(1, "a");
        var result = await grain.CreateSomeInput(input);
        return Ok(result);
    }

    [HttpPut]
    public async Task<IActionResult> Update(Guid id)
    {
        var grain  = grainFactory.GetGrain<ITestGrain>(id);
        var input  = new SomeInput(12, "a12");
        var result = await grain.UpdateSomeInput(input);
        return Ok(result);
    }

    [HttpDelete]
    public async Task<IActionResult> Delete(Guid id)
    {
        var grain  = grainFactory.GetGrain<ITestGrain>(id);
        var result = await grain.DeleteSomeInput();
        return Ok(result);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var grain  = grainFactory.GetGrain<ITestGrain>(id);
        var result = await grain.GetSomeInput();
        return Ok(result);
    }
}