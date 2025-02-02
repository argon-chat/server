#if DEBUG
    namespace Argon.Api.Controllers;

    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;

    [Route("api/[controller]"), ApiController]
    public class DevController(IGrainFactory grainFactory) : ControllerBase
    {
        [HttpGet("{userId:guid}/{channelId:guid}")]
        public async Task<ActionResult<List<ArgonMessage>>> GetMessages(Guid userId, Guid channelId, int count, int offset)
            => await grainFactory.GetGrain<IChannelGrain>(channelId).GetMessages(count, offset);

        [HttpPost("{userId:guid}/{channelId:guid}")]
        public async Task<ActionResult<ArgonMessage>> SendMessage(Guid userId, Guid channelId, ArgonMessage message)
            => await grainFactory.GetGrain<IChannelGrain>(channelId).SendMessage(message);
    }

#endif