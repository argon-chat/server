namespace Argon.Services.L1L2;

using Microsoft.Extensions.Caching.Hybrid;

public static class HybridCacheRegistration
{
    public static void AddHybridCache(this WebApplicationBuilder builder)
    {
        builder.Services.AddHybridCache(options =>
        {
            options.MaximumPayloadBytes = 1024 * 1024 * 512;
            options.MaximumKeyLength    = 512;
            options.DisableCompression  = true;

            options.DefaultEntryOptions = new HybridCacheEntryOptions
            {
                Expiration           = TimeSpan.FromHours(48),
                LocalCacheExpiration = TimeSpan.FromHours(48),
                Flags                = HybridCacheEntryFlags.DisableCompression
            };
        }).AddSerializerFactory<IonHybridCacheSerializerFactory>();
    }
}
