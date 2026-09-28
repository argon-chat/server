namespace ArgonComplexTest;

using System.Collections.Concurrent;
using Argon.Features.Expressions;
using ArgonContracts;
using Orleans.Runtime;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

/// <summary>
/// The shipped renderer, which a test can blind for one caller: to that caller the native libraries are missing,
/// as on a server built without them.
/// </summary>
/// <remarks>
/// Scoped by the caller the grain call carries rather than switched for the whole host, so the fixtures running
/// beside a blinded test keep their renderer.
/// </remarks>
public sealed class TestFirstFrameRenderer(FirstFrameRenderer inner) : IFirstFrameRenderer
{
    private readonly ConcurrentDictionary<Guid, byte> blind = new();

    /// <summary>Until disposed, calls made as <paramref name="userId"/> find no renderer.</summary>
    public IDisposable Blind(Guid userId)
    {
        blind[userId] = 0;
        return new Sight(() => blind.TryRemove(userId, out _));
    }

    public bool IsAvailable(ExpressionFormat format) => !IsBlind() && inner.IsAvailable(format);

    public Task<Image<Rgba32>?> RenderAsync(ReadOnlyMemory<byte> file, ExpressionFormat format, int width, int height, CancellationToken ct)
        => IsBlind() ? Task.FromResult<Image<Rgba32>?>(null) : inner.RenderAsync(file, format, width, height, ct);

    private bool IsBlind() => RequestContext.Get("$caller_user_id") is Guid caller && blind.ContainsKey(caller);

    private sealed class Sight(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
