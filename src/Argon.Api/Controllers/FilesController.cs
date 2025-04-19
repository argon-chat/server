namespace Argon.Controllers;

using Features.MediaStorage;
using Features.Pex;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

public class FilesController(
    IOptions<CdnOptions> cdnOptions,
    IContentDeliveryNetwork cdn,
    //IPermissionProvider permissions,
    IGrainFactory grainFactory,
    IContentTypeProvider contentType) : ControllerBase
{
    //// work only when cdn\storage set local disk or in memory
    //[HttpGet("/files/{fileId}")]
    //public async ValueTask<IActionResult> Files(
    //    [FromRoute] string nsPath,
    //    [FromRoute] Guid nsId,
    //    [FromRoute] string kind,
    //    [FromRoute] string shard,
    //    [FromRoute] string fileId)
    //{
    //    if (cdnOptions.Value.Storage.Kind == StorageKind.GenericS3)
    //        return BadRequest();

    //    var ns      = new StorageNameSpace(nsPath, nsId);
    //    var assetId = AssetId.FromFileId(fileId);
    //    var mem     = DiskContentStorage.OpenFileRead(ns, assetId);

    //    if (contentType.TryGetContentType(fileId, out var mime))
    //        return File(mem, mime);
    //    return File(mem, "application/octet-stream");
    //}

    [HttpPost("/files/server/{serverId:guid}/avatar"), Authorize(JwtBearerDefaults.AuthenticationScheme)]
    public async ValueTask<IActionResult> UploadServerAvatar([FromRoute] Guid serverId, IFormFile file)
    {
        var userId = HttpContext.GetUserId();

        //if (!await permissions.CanAccess(ArgonEntitlement.ManageServer, userId, serverId))
        //    return StatusCode(401);

        var assetId = AssetId.ServerFile(serverId, "png");
        var result  = await cdn.CreateAssetAsync(assetId, file);

        if (result.HasValue)
            return Ok(result);

        await grainFactory.GetGrain<IServerGrain>(serverId)
           .UpdateServer(new ServerInput(null, null, assetId.ToFileId()));

        return Ok(cdn.GenerateAssetUrl(assetId));
    }

    [HttpPost("/files/user/@me/avatar"), Authorize(JwtBearerDefaults.AuthenticationScheme)]
    public async ValueTask<IActionResult> UploadUserAvatar(IFormFile file)
    {
        var userId  = HttpContext.GetUserId();
        var assetId = AssetId.Avatar(userId);
        var result  = await cdn.CreateAssetAsync(assetId, file);

        if (result.HasValue)
            return Ok(result);

        await grainFactory.GetGrain<IUserGrain>(userId)
           .UpdateUser(new UserEditInput(null, null, assetId.ToFileId()));

        return Ok(cdn.GenerateAssetUrl(assetId));
    }
}