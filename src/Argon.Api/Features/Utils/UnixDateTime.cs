namespace Argon.Features.Web;

public static class UnixDateTime
{
    public static long ToUnixTimestamp(this DateTime value)
        => ConvertToUnixTimestamp(value);

    public static long ConvertToUnixTimestamp(DateTime date)
    {
        var origin = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
        var diff   = date.ToUniversalTime() - origin;
        return (long)Math.Floor(diff.TotalSeconds);
    }
}