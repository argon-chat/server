namespace Argon.Features.Web;

using System;

public static class UnixDateTime
{
    public static long ToUnixTimestamp(this DateTime value)
        => ((DateTimeOffset)value).ToUnixTimeSeconds();
}