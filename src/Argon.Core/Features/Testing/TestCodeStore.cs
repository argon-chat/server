namespace Argon.Features.Testing;

using System.Collections.Concurrent;

/// <summary>
/// In-memory implementation of ITestCodeStore for testing purposes.
/// </summary>
public class TestCodeStore : ITestCodeStore
{
    private readonly ConcurrentDictionary<(string Target, TestCodeType Type), StoredTestCode> _codes = new();

    public void StoreCode(string target, string code, TestCodeType type)
    {
        var key = (target.ToLowerInvariant(), type);
        _codes[key] = new StoredTestCode(code, DateTime.UtcNow);
    }

    public string? GetCode(string target, TestCodeType type)
    {
        var key = (target.ToLowerInvariant(), type);
        return _codes.TryGetValue(key, out var stored) ? stored.Code : null;
    }

    public async Task<string?> GetCodeAsync(string target, TestCodeType type, TimeSpan timeout, CancellationToken ct = default)
    {
        var key = (target.ToLowerInvariant(), type);
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (_codes.TryGetValue(key, out var stored))
                return stored.Code;

            await Task.Delay(50, ct);
        }

        return null;
    }

    public void Clear()
    {
        _codes.Clear();
    }

    private record StoredTestCode(string Code, DateTime StoredAt);
}

public static class TestCodeStoreExtensions
{
    public static void AddTestCodeStore(this IServiceCollection services)
    {
        services.AddSingleton<ITestCodeStore, TestCodeStore>();
    }
}
