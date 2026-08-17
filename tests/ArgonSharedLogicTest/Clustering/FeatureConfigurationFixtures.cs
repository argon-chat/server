namespace ArgonSharedLogicTest.Clustering;

using Argon.Features.Clustering;
using Microsoft.Extensions.Configuration;
using System.ComponentModel.DataAnnotations;

// Options and features used to exercise the configuration layer. Shaped around the three levels a
// model has for saying what a usable value is: the `required` keyword, data annotations, and its own
// rule.

/// <summary>Every level at once, so one section can produce every kind of finding.</summary>
public sealed class WidgetOptions : IValidatableFeatureOptions
{
    public required string Endpoint { get; set; }

    [Range(1, 10)]
    public int Retries { get; set; } = 3;

    public string? Fallback { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    public void Validate(IFeatureConfigurationReport report)
    {
        report.Require(Timeout > TimeSpan.Zero, nameof(Timeout), "must be positive");
        report.Prefer(Fallback is not null, nameof(Fallback), "unset, so a failure has nowhere to go");
    }
}

/// <summary>Nothing required, no rule — the shape most features have.</summary>
public sealed class GadgetOptions
{
    public bool Enabled { get; set; } = true;
    public int  Size    { get; set; } = 7;
}

/// <summary>Depends on a section it does not own, which is what <c>Read</c> exists for.</summary>
public sealed class SidecarOptions : IValidatableFeatureOptions
{
    public int Weight { get; set; }

    public void Validate(IFeatureConfigurationReport report)
    {
        if (!report.Read<GadgetOptions>("gadget").Enabled)
            return;

        report.Require(Weight > 0, nameof(Weight), "must be positive while the gadget is enabled");
    }
}

public sealed class WidgetFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("widget").Options<WidgetOptions>();
}

public sealed class GadgetFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Options<GadgetOptions>().Named("gadget");
}

public sealed class SidecarFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("sidecar").Requires<GadgetFeature>().Options<SidecarOptions>();
}

/// <summary>Two sections on one feature, the shape <c>reports</c> and <c>database</c> really have.</summary>
public sealed class PairFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("pair").Options<GadgetOptions>("pair").Options<SidecarOptions>("pair:sidecar");
}

public sealed class DuplicateSectionFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("dupe").Options<GadgetOptions>("same").Options<SidecarOptions>("same");
}

public sealed class ConfiguredRole : IArgonRole
{
    public static ArgonRoleId Id => new("configured");

    public bool IsClient => true;

    public void OnFeatures(IArgonFeatureRegistry features)
    {
        features.Add<WidgetFeature>();
        features.Add<SidecarFeature>();
    }
}

internal static class ConfigurationFixtures
{
    public static RoleDescriptor Role<TRole>() where TRole : IArgonRole
        => ArgonClusterCatalog.Build(new ClusterScanScope
        {
            Assemblies = [typeof(ConfigurationFixtures).Assembly, typeof(IArgonRole).Assembly],
            TypeFilter = type => type.Namespace?.StartsWith("ArgonSharedLogicTest") is true
                              || type.Assembly == typeof(IArgonRole).Assembly
        }).Require(TRole.Id);

    public static IConfiguration From(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder()
           .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
           .Build();

    public static IReadOnlyList<string> Codes(this FeatureConfigurationReportSet report)
        => report.Diagnostics.Select(d => d.Code).ToArray();
}
