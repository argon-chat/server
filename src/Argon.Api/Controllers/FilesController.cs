namespace Argon.Api.Controllers;

using ActualLab.Collections;
using Argon.Api.Features.MediaStorage.Storages;
using Contracts;
using Features.MediaStorage;
using Features.Pex;
using Grains.Interfaces;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

public class FilesController(
    IOptions<CdnOptions> cdnOptions,
    IContentDeliveryNetwork cdn,
    IPermissionProvider permissions,
    IGrainFactory grainFactory) : ControllerBase
{
    // work only when cdn\storage set local disk or in memory
    [HttpGet("/files/{nsPath}/{nsId:guid}/{kind}/{shard}/{fileId}")]
    public async ValueTask<IActionResult> Files(
        [FromRoute] string nsPath,
        [FromRoute] Guid nsId,
        [FromRoute] string kind,
        [FromRoute] string shard,
        [FromRoute] string fileId)
    {
        if (cdnOptions.Value.Storage.Kind == StorageKind.GenericS3)
            return BadRequest();

        var             ns      = new StorageNameSpace(nsPath, nsId);
        var             assetId = AssetId.FromFileId(fileId);
        var mem     = DiskContentStorage.OpenFileRead(ns, assetId);

        return File(mem, assetId.GetMime());
    }

    [HttpPost("/files/server/{serverId:guid}/avatar"), Authorize(JwtBearerDefaults.AuthenticationScheme)]
    public async ValueTask<IActionResult> UploadServerAvatar([FromRoute] Guid serverId, IFormFile file)
    {
        // TODO
        if (!permissions.CanAccess("server.avatar.upload", PropertyBag.Empty.Set(serverId)))
            return StatusCode(401);
        var assetId = AssetId.Avatar();
        var result  = await cdn.CreateAssetAsync(StorageNameSpace.ForServer(serverId), assetId, file);

        if (result.HasValue)
            return Ok(result);

        await grainFactory.GetGrain<IServerGrain>(serverId)
           .UpdateServer(new ServerInput(null, null, assetId.ToFileId()));

        return Ok();
    }

    [HttpPost("/files/user/{userId:guid}/avatar"), Authorize(JwtBearerDefaults.AuthenticationScheme)]
    public async ValueTask<IActionResult> UploadUserAvatar([FromRoute] Guid userId, IFormFile file)
    {
        // TODO
        if (!permissions.CanAccess("user.avatar.upload", PropertyBag.Empty.Set(userId)))
            return StatusCode(401);
        var assetId = AssetId.Avatar();
        var result  = await cdn.CreateAssetAsync(StorageNameSpace.ForUser(userId), assetId, file);

        if (result.HasValue)
            return Ok(result);

        await grainFactory.GetGrain<IUserGrain>(userId)
           .UpdateUser(new UserEditInput(null, null, assetId.ToFileId()));

        return Ok();
    }
}