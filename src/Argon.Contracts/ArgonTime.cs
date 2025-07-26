namespace Argon.Shared;

public static class ArgonTimeExtensions
{
    private static readonly Lazy<DateTimeOffset> ArgonEpoch = new(() => DateTimeOffset.Parse("01.01.2025 00:00:00 +00:00"));

    public static int ToArgonTimeSeconds(this DateTimeOffset dateTime)
        => (int)(dateTime - ArgonEpoch.Value).TotalSeconds;
}