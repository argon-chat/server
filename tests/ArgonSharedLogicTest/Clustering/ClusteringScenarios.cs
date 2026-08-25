namespace ArgonSharedLogicTest.Clustering;

using Argon.Features.Clustering;

/// <summary>
/// Each scenario is a container of roles, features and topologies. Tests scope discovery to one
/// container at a time via <see cref="ClusterScanScope.TypeFilter"/>, so a scenario that is
/// deliberately broken cannot poison the others despite sharing an assembly.
/// </summary>
public static class Scenario
{
    /// <summary>
    /// Scopes discovery to one scenario container plus an explicit list of fixture types. The list
    /// is spelled out per test rather than inferred, because which grains exist is exactly what
    /// rules like E2 (orphan grain) and the scanner's grain-boundary stop are being measured on.
    /// </summary>
    public static ClusterScanScope Scope(Type container, params Type[] fixtures)
        => ClusterScanScope.For(typeof(Scenario).Assembly,
            t => t.DeclaringType == container || fixtures.Contains(t));

    /// <summary>Scope with no roles at all — for exercising the scanner on its own.</summary>
    public static ClusterScanScope ScannerScope(params Type[] fixtures)
        => ClusterScanScope.For(typeof(Scenario).Assembly, fixtures.Contains);

    // ── a topology that validates clean ─────────────────────────────────────────────────────

    public static class Healthy
    {
        public sealed class SiloRole : IArgonRole
        {
            public static ArgonRoleId Id => new("silo");

            public bool IsClient      => false;
            public bool UsesReminders => true;

            public void OnGrainReferences(IGrainCollectionRegistry registry)
            {
                registry.AddToRef<AlphaGrain>();
                registry.AddToRef<BetaGrain>();
                registry.AddToRef<GammaGrain>();
                registry.AddToRef<OrphanGrain>();
                registry.AddToRef<StatefulGrain>();
            }
        }

        public sealed class Topology : IArgonTopology
        {
            public static string        Name  => "healthy";
            public static ArgonRoleId[] Roles => [new("silo")];
        }
    }

    // ── one grain per role, so every cross-role rule has something to fire on ────────────────

    public static class Split
    {
        /// <summary>Hosts Alpha, which calls Beta four times — W2's threshold — and W1's worker.</summary>
        public sealed class AlphaRole : IArgonRole
        {
            public static ArgonRoleId Id => new("alpha");

            public bool IsClient => false;

            public void OnGrainReferences(IGrainCollectionRegistry registry)
            {
                registry.AddToRef<AlphaGrain>();
                registry.AddToRef<OrphanGrain>();
                registry.AddToRef<StatefulGrain>();
            }
        }

        public sealed class BetaRole : IArgonRole
        {
            public static ArgonRoleId Id => new("beta");

            public bool IsClient      => false;
            public bool UsesReminders => true;

            public void OnGrainReferences(IGrainCollectionRegistry registry)
            {
                registry.AddToRef<BetaGrain>();
                registry.AddToRef<GammaGrain>();
            }
        }

        public sealed class Topology : IArgonTopology
        {
            public static string        Name  => "split";
            public static ArgonRoleId[] Roles => [new("alpha"), new("beta")];
        }
    }

    // ── the same split, with the cross-role trades declared ─────────────────────────────────

    public static class SplitQuiet
    {
        public sealed class AlphaRole : IArgonRole
        {
            public static ArgonRoleId Id => new("alpha-quiet");

            public bool IsClient => false;

            public void OnGrainReferences(IGrainCollectionRegistry registry)
            {
                registry.AddToRef<AlphaGrain>();
                registry.AddToRef<OrphanGrain>();
                registry.AddToRef<StatefulGrain>();
                registry.AcceptRemote<IBetaGrain>("beta owns the worker pool; the hop is the point");
            }
        }

        public sealed class BetaRole : IArgonRole
        {
            public static ArgonRoleId Id => new("beta-quiet");

            public bool IsClient      => false;
            public bool UsesReminders => true;

            public void OnGrainReferences(IGrainCollectionRegistry registry)
            {
                registry.AddToRef<BetaGrain>();
                registry.AddToRef<GammaGrain>();
            }
        }

        public sealed class Topology : IArgonTopology
        {
            public static string        Name  => "split-quiet";
            public static ArgonRoleId[] Roles => [new("alpha-quiet"), new("beta-quiet")];
        }
    }

    // ── calls a grain interface nothing implements ──────────────────────────────────────────

    public static class DeadInterface
    {
        public sealed class SiloRole : IArgonRole
        {
            public static ArgonRoleId Id => new("dead");

            public bool IsClient => false;

            public void OnGrainReferences(IGrainCollectionRegistry registry)
                => registry.AddToRef<DeltaGrain>();
        }

        public sealed class Topology : IArgonTopology
        {
            public static string        Name  => "dead";
            public static ArgonRoleId[] Roles => [new("dead")];
        }
    }

    // ── a client that wrongly claims to host grains, and an unhosted remindable ─────────────

    public static class Misconfigured
    {
        public sealed class ClientRole : IArgonRole
        {
            public static ArgonRoleId Id => new("bad-client");

            public bool IsClient => true;

            public void OnGrainReferences(IGrainCollectionRegistry registry)
                => registry.AddToRef<AlphaGrain>();
        }

        /// <summary>Hosts a remindable grain without declaring reminders, and starts one it does not host.</summary>
        public sealed class SiloRole : IArgonRole
        {
            public static ArgonRoleId Id => new("bad-silo");

            public bool IsClient => false;

            public void OnGrainReferences(IGrainCollectionRegistry registry)
            {
                registry.AddToRef<GammaGrain>();
                registry.AddStartupCall<IBetaGrain>();
            }
        }

        public sealed class Topology : IArgonTopology
        {
            public static string        Name  => "misconfigured";
            public static ArgonRoleId[] Roles => [new("bad-client"), new("bad-silo")];
        }
    }

    // ── analysis roots that static analysis cannot follow ───────────────────────────────────

    public static class Dynamic
    {
        public sealed class SiloRole : IArgonRole
        {
            public static ArgonRoleId Id => new("dynamic");

            public bool IsClient => false;

            public void OnGrainReferences(IGrainCollectionRegistry registry)
            {
                registry.AddToRef<GammaGrain>();
                registry.AddCallRoot<DynamicService>();
            }
        }

        public sealed class WaivedRole : IArgonRole
        {
            public static ArgonRoleId Id => new("dynamic-waived");

            public bool IsClient => false;

            public void OnGrainReferences(IGrainCollectionRegistry registry)
            {
                registry.AddToRef<GammaGrain>();
                registry.AddCallRoot<DynamicService>();
                registry.AddDynamicRef<IGammaGrain>();
                registry.AllowUnresolved<DynamicService>("resolves IGammaGrain from a Type switch");
            }
        }
    }

    // ── role composition ────────────────────────────────────────────────────────────────────

    public static class Composed
    {
        public sealed class LeafRole : IArgonRole
        {
            public static ArgonRoleId Id => new("leaf");

            public bool IsClient => false;

            public void OnGrainReferences(IGrainCollectionRegistry registry)
                => registry.AddToRef<GammaGrain>();

            public void OnFeatures(IArgonFeatureRegistry features)
                => features.Add<StorageFeature>();
        }

        public sealed class TrunkRole : IArgonRole
        {
            public static ArgonRoleId Id => new("trunk");

            public bool IsClient      => false;
            public bool UsesReminders => true;

            public void OnGrainReferences(IGrainCollectionRegistry registry)
            {
                registry.AddToRef<BetaGrain>();
                registry.Include<LeafRole>();
            }

            public void OnFeatures(IArgonFeatureRegistry features)
                => features.Add<ApiFeature>();
        }
    }

    // ── mutually including roles ────────────────────────────────────────────────────────────

    public static class CyclicRoles
    {
        public sealed class LeftRole : IArgonRole
        {
            public static ArgonRoleId Id => new("left");

            public bool IsClient => false;

            public void OnGrainReferences(IGrainCollectionRegistry registry)
                => registry.Include<RightRole>();
        }

        public sealed class RightRole : IArgonRole
        {
            public static ArgonRoleId Id => new("right");

            public bool IsClient => false;

            public void OnGrainReferences(IGrainCollectionRegistry registry)
                => registry.Include<LeftRole>();
        }
    }

    // ── two roles claiming the same id ──────────────────────────────────────────────────────

    public static class DuplicateIds
    {
        public sealed class FirstRole : IArgonRole
        {
            public static ArgonRoleId Id => new("clash");
            public bool IsClient => false;
        }

        public sealed class SecondRole : IArgonRole
        {
            public static ArgonRoleId Id => new("clash");
            public bool IsClient => false;
        }
    }

    // ── features ────────────────────────────────────────────────────────────────────────────

    public sealed class StorageFeature : IArgonFeature
    {
        public static void Describe(IFeatureDescriptor d)
            => d.Named("storage");

        public void Configure(ArgonFeatureContext ctx)
            => Configured.Add("storage");
    }

    public sealed class AuthFeature : IArgonFeature
    {
        public static void Describe(IFeatureDescriptor d)
            => d.Named("auth").Requires<StorageFeature>();

        public void Configure(ArgonFeatureContext ctx)
            => Configured.Add("auth");
    }

    /// <summary>Requires auth transitively and roots analysis at a service that reaches Gamma.</summary>
    public sealed class ApiFeature : IArgonFeature
    {
        public static void Describe(IFeatureDescriptor d)
            => d.Named("api")
                .Requires<AuthFeature>()
                .GrainRoots(g => g.AddCallRoot<IndirectService>());

        public void Configure(ArgonFeatureContext ctx)
            => Configured.Add("api");
    }

    public sealed class LegacyAuthFeature : IArgonFeature
    {
        public static void Describe(IFeatureDescriptor d)
            => d.Named("legacy-auth").Conflicts<AuthFeature>();

        public void Configure(ArgonFeatureContext ctx)
        {
        }
    }

    public sealed class LoopAFeature : IArgonFeature
    {
        public static void Describe(IFeatureDescriptor d)
            => d.Named("loop-a").Requires<LoopBFeature>();

        public void Configure(ArgonFeatureContext ctx)
        {
        }
    }

    public sealed class LoopBFeature : IArgonFeature
    {
        public static void Describe(IFeatureDescriptor d)
            => d.Named("loop-b").Requires<LoopAFeature>();

        public void Configure(ArgonFeatureContext ctx)
        {
        }
    }

    /// <summary>Records feature configure order for assertions.</summary>
    public static List<string> Configured { get; } = [];
}
