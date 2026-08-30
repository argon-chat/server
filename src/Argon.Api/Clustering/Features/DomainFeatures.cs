namespace Argon.Api.Clustering;

using Argon.Features.Integrations.Phones;
using Argon.Features.Logic;
using Argon.Features.Moderation;
using Argon.Features.Storage;
using Argon.Sfu;

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
        => d.Describing("distributed id generation").Options<SnowflakeOptions>("Snowflake");

    public void Configure(ArgonFeatureContext ctx)
    {
        var options = ctx.Options<SnowflakeOptions>();

        ctx.Services.AddSnowflakeUniqueId(snowflake =>
        {
            snowflake.DataCenterId  = options.DataCenterId;
            snowflake.UseConsoleLog = options.UseConsoleLog;
        });
    }
}

public sealed class MessagesFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("message storage and fan-out")
            .Requires<DatabaseFeature>()
            .Requires<SnowflakeFeature>()
            .Requires<CacheFeature>()
            .Options<MessagesOptions>("Messages");

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddMessagesLayout();
}

/// <summary>
/// User presence tracking, and the bus it needs to say anything about it.
/// </summary>
/// <remarks>
/// <para><b>The dependency on the bus is declared, and it was not.</b> This feature registers
/// <c>IUserSessionNotifier</c>, whose only implementation resolves <c>AppHubServer</c> — and does it
/// inside the call, out of a scope it opens itself, because the notifier is a singleton and the hub
/// server is scoped. Nothing in a constructor names it, so nothing could see the requirement: not
/// the feature graph, not the role wiring, and not the test that walks every hosted grain's
/// constructor looking for services its role forgot to register.</para>
///
/// <para>It came due on <c>voice</c>, which takes this feature for <c>CallGrain</c> and
/// <c>SipGrain</c> and never took the bus. Hanging up a call answered <c>500</c> with
/// "No service for type AppHubServer has been registered" — raised inside the notify, after the call
/// had already been torn down, so the hangup half-succeeded and the caller was told it failed.</para>
///
/// <para>Adding the bus to the role would have fixed that role. Declaring it here fixes every role
/// that takes presence, including the next one somebody adds.</para>
/// </remarks>
public sealed class PresenceFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("user presence tracking")
            .Requires<CacheFeature>()
            .Requires<RealtimeBusFeature>();

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
        => d.Describing("one-time codes")
            .Requires<CacheFeature>()
            .Options<PhoneVerificationOptions>("Phone");

    public void Configure(ArgonFeatureContext ctx)
    {
        ctx.Builder.AddOtpCodes();
        ctx.Services.AddTestCodeStore();
    }
}

public sealed class SocialFeature : IArgonFeature
{
    // No configuration: the Telegram binder and its options are commented out in Argon.Core, so there
    // is nothing to bind. Declaring an empty section would only invite someone to fill it in.
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("Telegram and other social binders").Requires<HttpClientFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddSocialIntegrations();
}

public sealed class FileStorageFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("S3 object storage")
            .Requires<VaultFeature>()
            .Requires<DatabaseFeature>()
            .Options<StorageOptions>(StorageOptions.SectionName)
            .Options<FileLimitsOptions>(FileLimitsOptions.SectionName);

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddFileStorageFeature();
}

public sealed class FileGcFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("file-gc")
            .Describing("collects orphaned blobs")
            .Requires<FileStorageFeature>()
            .Options<FileGcOptions>("FileGc");

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Services.AddHostedService<FileGcService>();
}

public sealed class ContentModerationFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("ONNX image classification — resident models, the heaviest thing in the tree")
            .Requires<FileStorageFeature>()
            .Options<ModeratorConfig>(ModeratorConfig.SectionName);

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddContentModeration();
}

public sealed class ReportSystemFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("reports")
            .Requires<DatabaseFeature>()
            .Options<ReportSystemOptions>(ReportSystemOptions.SectionName)
            .Options<TrustScoringOptions>(TrustScoringOptions.SectionName);

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddReportSystem();
}

public sealed class SfuFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("LiveKit selective forwarding unit")
            .Requires<VaultFeature>()
            .Options<CallKitOptions>("CallKit");

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddSelectiveForwardingUnit();
}

public sealed class KlipyFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("GIF search")
            .Requires<HttpClientFeature>()
            .Options<KlipyOptions>(KlipyOptions.SectionName);

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddKlipyFeature();
}

public sealed class GeoIpFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("geoip").Options<GeoIpOptions>("GeoIp");

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddGeoIpSupport();
}

public sealed class AccountDeletionFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Requires<DatabaseFeature>()
            .Options<AccountDeletionOptions>(AccountDeletionOptions.SectionName);

    // Nothing to register: the framework binds and validates what this feature declares.
}
