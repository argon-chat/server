namespace Argon.Api.Clustering;

using global::Sentry.Infrastructure;

public sealed class RepositoriesFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("EF repositories and pre-migration handlers").Requires<DatabaseFeature>();

    public void Configure(ArgonFeatureContext ctx)
    {
        ctx.Builder.AddBeforeMigrations();
        ctx.Builder.AddEfRepositories();
    }
}

public sealed class PermissionsFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("entitlement evaluation").Requires<DatabaseFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddArgonPermissions();
}

public sealed class ArchetypeCacheFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Requires<CacheFeature>().Requires<PermissionsFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddArchetypesCache();
}

public sealed class SnowflakeFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("distributed id generation");

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Services.AddSnowflakeUniqueId(options =>
        {
            options.DataCenterId  = 1;
            options.UseConsoleLog = true;
        });
}

public sealed class MessagesFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("message storage and fan-out")
            .Requires<DatabaseFeature>()
            .Requires<SnowflakeFeature>()
            .Requires<CacheFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddMessagesLayout();
}

public sealed class PresenceFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("user presence tracking").Requires<CacheFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddUserPresenceFeature();
}

public sealed class NotificationsFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Requires<CacheFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddNotificationFeature();
}

public sealed class OtpFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("one-time codes").Requires<CacheFeature>();

    public void Configure(ArgonFeatureContext ctx)
    {
        ctx.Builder.AddOtpCodes();
        ctx.Services.AddTestCodeStore();
    }
}

public sealed class SocialFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("Telegram and other social binders").Requires<HttpClientFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddSocialIntegrations();
}

public sealed class FileStorageFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("S3 object storage").Requires<VaultFeature>().Requires<DatabaseFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddFileStorageFeature();
}

public sealed class FileGcFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("file-gc").Describing("collects orphaned blobs").Requires<FileStorageFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Services.AddHostedService<FileGcService>();
}

public sealed class ContentModerationFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("ONNX image classification — resident models, the heaviest thing in the tree")
            .Requires<FileStorageFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddContentModeration();
}

public sealed class ReportSystemFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("reports").Requires<DatabaseFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddReportSystem();
}

public sealed class SfuFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("LiveKit selective forwarding unit").Requires<VaultFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddSelectiveForwardingUnit();
}

public sealed class KlipyFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("GIF search").Requires<HttpClientFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddKlipyFeature();
}

public sealed class GeoIpFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("geoip");

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddGeoIpSupport();
}

public sealed class AccountDeletionFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Requires<DatabaseFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Services.Configure<AccountDeletionOptions>(
            ctx.Configuration.GetSection(AccountDeletionOptions.SectionName));
}
