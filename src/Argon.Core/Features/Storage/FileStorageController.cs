namespace Argon.Features.Storage;

using Argon.Api.Grains.Interfaces;
using Microsoft.AspNetCore.Mvc;

[ApiController, Route("/api/files")]
[Authorize]
public class FileStorageController(IClusterClient client) : ControllerBase
{
    private Guid UserId => Guid.Parse(User.FindFirst("uid")!.Value);

    [HttpPost("upload")]
    public async Task<IActionResult> RequestUpload([FromBody] FileUploadHttpRequest request, CancellationToken ct)
    {
        var grain = client.GetGrain<IFileStorageGrain>(UserId);
        var result = await grain.RequestUploadAsync(new FileUploadRequest(
            request.Purpose,
            request.ContentType,
            request.FileSize,
            request.SpaceId,
            request.ChannelId), ct);

        return Ok(new
        {
            blobId = result.BlobId,
            fileId = result.FileId,
            url    = result.Url,
            fields = result.Fields,
            ttl    = result.TtlSeconds
        });
    }

    [HttpPost("{blobId:guid}/finalize")]
    public async Task<IActionResult> FinalizeUpload([FromRoute] Guid blobId, CancellationToken ct)
    {
        var grain = client.GetGrain<IFileStorageGrain>(UserId);
        var result = await grain.FinalizeUploadAsync(blobId, ct);
        return Ok(result);
    }

    [HttpGet("{fileId:guid}")]
    public async Task<IActionResult> GetFileInfo([FromRoute] Guid fileId, CancellationToken ct)
    {
        var grain = client.GetGrain<IFileStorageGrain>(UserId);
        var result = await grain.GetFileInfoAsync(fileId, ct);
        if (result is null) return NotFound();
        return Ok(result);
    }

    [HttpGet("{fileId:guid}/url")]
    public async Task<IActionResult> GetDownloadUrl([FromRoute] Guid fileId, CancellationToken ct)
    {
        var grain = client.GetGrain<IFileStorageGrain>(UserId);
        var url = await grain.GetDownloadUrlAsync(fileId, ct);
        if (url is null) return NotFound();
        return Ok(new { url });
    }

    [HttpPost("{fileId:guid}/increment")]
    public async Task<IActionResult> IncrementRef([FromRoute] Guid fileId, CancellationToken ct)
    {
        var grain = client.GetGrain<IFileStorageGrain>(UserId);
        await grain.IncrementRefAsync(fileId, ct);
        return NoContent();
    }

    [HttpPost("{fileId:guid}/decrement")]
    public async Task<IActionResult> DecrementRef([FromRoute] Guid fileId, CancellationToken ct)
    {
        var grain = client.GetGrain<IFileStorageGrain>(UserId);
        await grain.DecrementRefAsync(fileId, ct);
        return NoContent();
    }
}

public record FileUploadHttpRequest
{
    public required FilePurpose Purpose     { get; init; }
    public required string      ContentType { get; init; }
    public required long        FileSize    { get; init; }
    public          Guid?       SpaceId     { get; init; }
    public          Guid?       ChannelId   { get; init; }
}
