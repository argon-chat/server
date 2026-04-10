namespace Argon.Features.BotApi;

/// <summary>
/// Computes deterministic hashes of Bot API contract surfaces and verifies
/// them against declared <see cref="StableContractAttribute"/> values at startup.
/// </summary>
public static class BotContractVerifier
{
    /// <summary>
    /// Scans all <see cref="IBotInterface"/> implementations, computes their
    /// contract hash from <see cref="BotRouteAttribute"/> metadata, and checks
    /// against <see cref="StableContractAttribute"/>.
    /// Returns a list of mismatches (empty = all good).
    /// </summary>
    public static List<ContractMismatch> Verify()
    {
        var mismatches = new List<ContractMismatch>();

        foreach (var (type, interfaceAttr, _) in DiscoverInterfaces())
        {
            var stable = type.GetCustomAttribute<StableContractAttribute>();
            if (stable is null)
                continue; // not frozen yet, skip

            var computed = ComputeContractHash(type);

            if (!string.Equals(stable.ContractHash, computed, StringComparison.OrdinalIgnoreCase))
            {
                mismatches.Add(new ContractMismatch(
                    $"{interfaceAttr.Name}/v{interfaceAttr.Version}",
                    stable.ContractHash,
                    computed));
            }
        }

        return mismatches;
    }

    /// <summary>
    /// Generates the full contract manifest for all discovered bot interfaces.
    /// Used by the CLI tool for hash computation and documentation.
    /// </summary>
    public static List<InterfaceManifest> GenerateManifest()
    {
        var result = new List<InterfaceManifest>();

        foreach (var (type, interfaceAttr, deprecated) in DiscoverInterfaces())
        {
            var desc = type.GetCustomAttribute<BotDescriptionAttribute>();
            var errors = type.GetCustomAttributes<BotErrorAttribute>()
               .GroupBy(e => e.Route)
               .ToDictionary(g => g.Key, g => g.Select(e => new ErrorManifest(e.Status, e.Code, e.Description))
                   .OrderBy(e => e.Status).ThenBy(e => e.Code).ToList());
            var routes = type.GetCustomAttributes<BotRouteAttribute>()
               .Select(r => new RouteManifest(
                    r.Method,
                    r.Path,
                    r.Description,
                    r.RequestType is not null ? GetSimpleTypeName(r.RequestType) : null,
                    r.ResponseType is not null ? GetSimpleTypeName(r.ResponseType) : null,
                    BuildWebTypeShape(r.RequestType),
                    BuildWebTypeShape(r.ResponseType),
                    r.Permission,
                    r.IsPrivileged,
                    errors.TryGetValue(r.Path, out var routeErrors) ? routeErrors : null))
               .OrderBy(r => r.Path)
               .ThenBy(r => r.Method)
               .ToList();

            var hash   = ComputeContractHash(type);
            var stable = type.GetCustomAttribute<StableContractAttribute>();

            result.Add(new InterfaceManifest(
                interfaceAttr.Name,
                interfaceAttr.Version,
                hash,
                stable?.ContractHash,
                stable is not null,
                deprecated is not null,
                desc?.Description,
                routes));
        }

        return result.OrderBy(x => x.Name).ThenBy(x => x.Version).ToList();
    }

    /// <summary>
    /// Generates the complete documentation manifest: interfaces, intents, events, and rate limits.
    /// All data is derived from backend source-of-truth enums and configuration.
    /// </summary>
    public static DocsManifest GenerateDocsManifest()
    {
        var interfaces = GenerateManifest();

        // Build intent → event names from canonical EventIntents map
        var intentEvents = new Dictionary<BotIntent, List<string>>();
        foreach (var (eventType, intent) in BotEventMapping.EventIntents)
        {
            if (intent is null) continue;
            if (!intentEvents.TryGetValue(intent.Value, out var list))
                intentEvents[intent.Value] = list = [];
            list.Add(eventType.ToString());
        }

        // Build intents list from enum
        var privileged = BotIntent.AllPrivileged;
        var intents = Enum.GetValues<BotIntent>()
           .Where(i => i != BotIntent.None && i != BotIntent.AllNonPrivileged && i != BotIntent.AllPrivileged)
           .Select(i =>
            {
                var bit = (int)Math.Log2((long)i);
                intentEvents.TryGetValue(i, out var events);
                return new IntentManifest(
                    i.ToString(),
                    bit,
                    (long)i,
                    privileged.HasFlag(i),
                    events?.OrderBy(e => e).ToArray() ?? []);
            })
           .OrderBy(i => i.Bit)
           .ToList();

        // Build events list from canonical EventIntents map + payload type shapes
        // Reverse map: BotEventType → List<(UnionKey, PayloadType)>
        // Uses PayloadOverrides when available for clean Bot API surface
        var eventPayloadTypes = ResolveEventPayloadTypes();
        var reverseMap = new Dictionary<BotEventType, List<(string UnionKey, Type PayloadType)>>();
        foreach (var (unionKey, (eventType, _)) in BotEventMapping.Map)
        {
            // Prefer dedicated Bot API payload type over raw IArgonEvent type
            Type? payloadType;
            if (BotEventMapping.PayloadOverrides.TryGetValue(unionKey, out var overrideType))
                payloadType = overrideType;
            else
                eventPayloadTypes.TryGetValue(unionKey, out payloadType);

            if (payloadType is not null)
            {
                if (!reverseMap.TryGetValue(eventType, out var list))
                    reverseMap[eventType] = list = [];
                list.Add((unionKey, payloadType));
            }
        }

        var events = Enum.GetValues<BotEventType>()
           .Select(e =>
            {
                BotEventMapping.EventIntents.TryGetValue(e, out var intent);
                var category = intent?.ToString() ?? "Connection";
                BotEventMapping.Descriptions.TryGetValue(e, out var desc);

                string? payloadTypeName = null;
                List<TypeProperty>? payloadTypeShape = null;
                List<EventPayloadVariant>? payloadVariants = null;

                if (reverseMap.TryGetValue(e, out var payloadSources))
                {
                    if (payloadSources.Count == 1)
                    {
                        var (unionKey, payloadType) = payloadSources[0];
                        payloadTypeName = payloadType.Name;
                        payloadTypeShape = BuildEventPayloadShape(payloadType);
                    }
                    else
                    {
                        // N:1 mapping (e.g. PresenceUpdate ← multiple IArgonEvent types)
                        payloadVariants = payloadSources
                           .Select(ps => new EventPayloadVariant(
                                ps.PayloadType.Name,
                                BuildEventPayloadShape(ps.PayloadType) ?? []))
                           .ToList();
                    }
                }

                return new EventManifest(
                    e.ToString(),
                    intent?.ToString(),
                    desc,
                    category,
                    payloadTypeName,
                    payloadTypeShape,
                    payloadVariants);
            })
           .ToList();

        // Build rate limits from hardcoded defaults
        var opts = new BotRateLimitOptions();
        var rateLimits = new List<RateLimitManifest>
        {
            new("*", opts.Global.TokenLimit, opts.Global.TokensPerPeriod,
                FormatPeriod(opts.Global.ReplenishmentPeriod))
        };
        foreach (var (name, bucket) in opts.Interfaces.OrderBy(kv => kv.Key))
        {
            rateLimits.Add(new RateLimitManifest(
                name, bucket.TokenLimit, bucket.TokensPerPeriod,
                FormatPeriod(bucket.ReplenishmentPeriod)));
        }

        return new DocsManifest(interfaces, intents, events, rateLimits);
    }

    private static string FormatPeriod(TimeSpan ts)
        => ts.TotalSeconds < 60 ? $"{ts.TotalSeconds}s" : $"{ts.TotalMinutes}m";

    private static string GetSimpleTypeName(Type type)
    {
        var name = type.Name.Contains('`') ? type.Name[..type.Name.IndexOf('`')] : type.Name;
        // Strip nested type prefix (e.g. "ChannelsV1+CreateChannelRequest" → "CreateChannelRequest")
        if (name.Contains('+'))
            name = name[(name.LastIndexOf('+') + 1)..];
        return name;
    }

    /// <summary>
    /// Computes SHA-256 hash of the API surface for a given interface type.
    /// Deterministic: same routes + types = same hash.
    /// </summary>
    public static string ComputeContractHash(Type interfaceType)
    {
        var routes = interfaceType.GetCustomAttributes<BotRouteAttribute>()
           .OrderBy(r => r.Path)
           .ThenBy(r => r.Method);

        var sb = new StringBuilder();

        // Include interface identity
        var attr = interfaceType.GetCustomAttribute<BotInterfaceAttribute>()!;
        // Use explicit '\n' — AppendLine uses Environment.NewLine which differs across OS
        sb.Append($"INTERFACE:{attr.Name}:v{attr.Version}\n");

        foreach (var route in routes)
        {
            sb.Append($"ROUTE:{route.Method}:{route.Path}\n");

            if (route.RequestType is not null)
            {
                sb.Append($"  REQUEST:{route.RequestType.FullName}\n");
                AppendTypeShape(sb, route.RequestType, "    ");
            }

            if (route.ResponseType is not null)
            {
                sb.Append($"  RESPONSE:{route.ResponseType.FullName}\n");
                AppendTypeShape(sb, route.ResponseType, "    ");
            }
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(bytes);
    }

    private static void AppendTypeShape(StringBuilder sb, Type type, string indent, HashSet<Type>? visited = null)
    {
        visited ??= [];
        if (!visited.Add(type))
        {
            sb.Append($"{indent}[circular:{type.Name}]\n");
            return;
        }

        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
           .OrderBy(p => p.Name, StringComparer.Ordinal);

        foreach (var prop in props)
        {
            var propTypeName = GetCanonicalTypeName(prop.PropertyType);
            sb.Append($"{indent}{prop.Name}:{propTypeName}\n");

            // Recurse into non-primitive, non-collection custom types
            var innerType = GetInnerType(prop.PropertyType);
            if (innerType is not null && !IsSimpleType(innerType) && innerType.Assembly.FullName?.StartsWith("Argon") == true)
                AppendTypeShape(sb, innerType, indent + "  ", visited);
        }
    }

    private static string? SerializeTypeShape(Type? type)
    {
        if (type is null) return null;
        var sb = new StringBuilder();
        AppendTypeShape(sb, type, "");
        return sb.ToString().TrimEnd();
    }

    // --- Structured type shapes for documentation (camelCase, JSON types) ---

    private static List<TypeProperty>? BuildWebTypeShape(Type? type)
    {
        if (type is null) return null;
        var result = BuildWebTypeProperties(type, []);
        return result.Count > 0 ? result : null;
    }

    private static List<TypeProperty> BuildWebTypeProperties(Type type, HashSet<Type> visited)
    {
        if (!visited.Add(type))
            return [];

        var result = new List<TypeProperty>();
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
           .OrderBy(p => p.Name, StringComparer.Ordinal);

        foreach (var prop in props)
        {
            var camelName = char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..];
            var (innerType, typeName, isArray, isNullable) = DecomposeWebType(prop.PropertyType);

            List<TypeProperty>? children = null;
            var isCircular = false;
            string[]? enumValues = null;
            var isUnion = false;
            string? discriminatorField = null;
            string? discriminatorType = null;
            List<UnionVariant>? variants = null;

            if (innerType.IsEnum)
            {
                enumValues = Enum.GetNames(innerType)
                    .Select(ToCamelCase)
                    .ToArray();
            }
            else if (IsIonUnionType(innerType))
            {
                isUnion = true;
                (discriminatorField, discriminatorType, variants) = BuildUnionVariants(innerType, visited);
            }
            else if (!IsSimpleType(innerType) && innerType.Assembly.FullName?.StartsWith("Argon") == true)
            {
                if (visited.Contains(innerType))
                    isCircular = true;
                else
                    children = BuildWebTypeProperties(innerType, visited);
            }

            result.Add(new TypeProperty(camelName, typeName, isArray, isNullable, isCircular, children, enumValues,
                isUnion, discriminatorField, discriminatorType, variants));
        }

        visited.Remove(type);
        return result;
    }

    private static readonly HashSet<string> UnionInternalProps = ["UnionKey", "UnionIndex"];

    /// <summary>
    /// Checks if a type is an Ion discriminated union (interface implementing IIonUnion&lt;T&gt;).
    /// </summary>
    private static bool IsIonUnionType(Type type)
        => type.IsInterface && type.GetInterfaces()
           .Any(i => i.IsGenericType && i.GetGenericTypeDefinition().Name.StartsWith("IIonUnion"));

    /// <summary>
    /// Builds union variant metadata for a given IIonUnion&lt;T&gt; interface type.
    /// Scans assemblies for all concrete implementations and extracts their property shapes.
    /// </summary>
    private static (string? DiscriminatorField, string? DiscriminatorType, List<UnionVariant> Variants)
        BuildUnionVariants(Type unionInterfaceType, HashSet<Type> visited)
    {
        var concreteTypes = AppDomain.CurrentDomain.GetAssemblies()
           .Where(a => a.FullName?.StartsWith("Argon") == true)
           .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch { return []; }
            })
           .Where(t => t is { IsClass: true, IsAbstract: false } && unionInterfaceType.IsAssignableFrom(t))
           .OrderBy(t => t.Name, StringComparer.Ordinal)
           .ToList();

        // Detect discriminator field: look for an Enum-typed property on the concrete types
        // that serves as the union discriminator (e.g., "Type" with EntityType enum for IMessageEntity)
        string? discriminatorField = null;
        string? discriminatorType = null;

        if (concreteTypes.Count > 0)
        {
            var firstType = concreteTypes[0];
            var enumProp = firstType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
               .Where(p => p.PropertyType.IsEnum && !UnionInternalProps.Contains(p.Name))
               .FirstOrDefault();

            if (enumProp is not null)
            {
                discriminatorField = char.ToLowerInvariant(enumProp.Name[0]) + enumProp.Name[1..];
                discriminatorType = enumProp.PropertyType.Name;
            }
        }

        var variants = new List<UnionVariant>();
        foreach (var concreteType in concreteTypes)
        {
            // Get discriminator value from enum property if available
            string? discriminatorValue = null;
            if (discriminatorField is not null)
            {
                var enumProp = concreteType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                   .FirstOrDefault(p => p.PropertyType.IsEnum && !UnionInternalProps.Contains(p.Name));

                if (enumProp is not null)
                {
                    // Try to get default value from parameterless instance or static field
                    try
                    {
                        // For records with a default enum value, look for the matching enum name
                        // Convention: MessageEntityBold → EntityType.Bold, MessageEntityMention → EntityType.Mention
                        var typeName = concreteType.Name;
                        var enumType = enumProp.PropertyType;
                        foreach (var enumName in Enum.GetNames(enumType))
                        {
                            if (typeName.EndsWith(enumName, StringComparison.OrdinalIgnoreCase)
                                || typeName.Contains(enumName, StringComparison.OrdinalIgnoreCase))
                            {
                                discriminatorValue = ToCamelCase(enumName);
                                break;
                            }
                        }
                    }
                    catch
                    {
                        // Best-effort
                    }
                }
            }

            discriminatorValue ??= ToCamelCase(concreteType.Name);

            // Build properties excluding union internals
            var variantProps = concreteType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
               .Where(p => !UnionInternalProps.Contains(p.Name))
               .OrderBy(p => p.Name, StringComparer.Ordinal)
               .Select(p =>
                {
                    var camelName = char.ToLowerInvariant(p.Name[0]) + p.Name[1..];
                    var (innerType, webTypeName, isArray, isNullable) = DecomposeWebType(p.PropertyType);

                    List<TypeProperty>? children = null;
                    var isCircular = false;
                    string[]? enumValues = null;

                    if (innerType.IsEnum)
                    {
                        enumValues = Enum.GetNames(innerType).Select(ToCamelCase).ToArray();
                    }
                    else if (!IsSimpleType(innerType) && innerType.Assembly.FullName?.StartsWith("Argon") == true)
                    {
                        if (visited.Contains(innerType))
                            isCircular = true;
                        else
                            children = BuildWebTypeProperties(innerType, visited);
                    }

                    return new TypeProperty(camelName, webTypeName, isArray, isNullable, isCircular, children, enumValues);
                })
               .ToList();

            variants.Add(new UnionVariant(concreteType.Name, discriminatorValue, variantProps));
        }

        return (discriminatorField, discriminatorType, variants);
    }

    /// <summary>
    /// Scans all Argon assemblies for concrete IArgonEvent implementations.
    /// Returns a map of union key (type name) → Type.
    /// </summary>
    private static Dictionary<string, Type> ResolveEventPayloadTypes()
    {
        var argonEventType = AppDomain.CurrentDomain.GetAssemblies()
           .Where(a => a.FullName?.StartsWith("Argon") == true)
           .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch { return []; }
            })
           .FirstOrDefault(t => t is { IsInterface: true, Name: "IArgonEvent" });

        if (argonEventType is null)
            return new Dictionary<string, Type>();

        return AppDomain.CurrentDomain.GetAssemblies()
           .Where(a => a.FullName?.StartsWith("Argon") == true)
           .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch { return []; }
            })
           .Where(t => t is { IsClass: true, IsAbstract: false } && argonEventType.IsAssignableFrom(t))
           .ToDictionary(t => t.Name);
    }

    /// <summary>
    /// Builds the payload shape for an IArgonEvent record type,
    /// excluding union internal properties (UnionKey, UnionIndex).
    /// </summary>
    private static List<TypeProperty>? BuildEventPayloadShape(Type eventType)
    {
        var visited = new HashSet<Type>();
        if (!visited.Add(eventType))
            return null;

        var result = new List<TypeProperty>();
        var props = eventType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
           .Where(p => !UnionInternalProps.Contains(p.Name))
           .OrderBy(p => p.Name, StringComparer.Ordinal);

        foreach (var prop in props)
        {
            var camelName = char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..];
            var (innerType, typeName, isArray, isNullable) = DecomposeWebType(prop.PropertyType);

            List<TypeProperty>? children = null;
            var isCircular = false;
            string[]? enumValues = null;
            var isUnion = false;
            string? discriminatorField = null;
            string? discriminatorType = null;
            List<UnionVariant>? variants = null;

            if (innerType.IsEnum)
            {
                enumValues = Enum.GetNames(innerType).Select(ToCamelCase).ToArray();
            }
            else if (IsIonUnionType(innerType))
            {
                isUnion = true;
                (discriminatorField, discriminatorType, variants) = BuildUnionVariants(innerType, visited);
            }
            else if (!IsSimpleType(innerType) && innerType.Assembly.FullName?.StartsWith("Argon") == true)
            {
                if (visited.Contains(innerType))
                    isCircular = true;
                else
                    children = BuildWebTypeProperties(innerType, visited);
            }

            result.Add(new TypeProperty(camelName, typeName, isArray, isNullable, isCircular, children, enumValues,
                isUnion, discriminatorField, discriminatorType, variants));
        }

        visited.Remove(eventType);
        return result.Count > 0 ? result : null;
    }

    private static (Type Inner, string TypeName, bool IsArray, bool IsNullable) DecomposeWebType(Type type)
    {
        var isNullable = false;
        var isArray    = false;

        // Unwrap Nullable<T>
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            isNullable = true;
            type = type.GetGenericArguments()[0];
        }

        // Unwrap collection types
        if (type.IsGenericType)
        {
            var def     = type.GetGenericTypeDefinition();
            var defName = def.Name;
            if (def == typeof(List<>) || def == typeof(IReadOnlyList<>)
                || defName.StartsWith("ICollection") || defName.StartsWith("IonArray"))
            {
                isArray = true;
                type = type.GetGenericArguments()[0];
            }
        }
        else if (type.IsArray)
        {
            isArray = true;
            type = type.GetElementType()!;
        }

        return (type, GetWebBaseTypeName(type), isArray, isNullable);
    }

    private static string GetWebBaseTypeName(Type type)
    {
        if (type.IsEnum) return "string";

        return type.Name switch
        {
            "String"         => "string",
            "Int32"          => "int32",
            "UInt32"         => "uint32",
            "Int64"          => "int64",
            "UInt64"         => "uint64",
            "Int16"          => "int16",
            "Single"         => "float",
            "Double"         => "double",
            "Decimal"        => "decimal",
            "Boolean"        => "boolean",
            "Guid"           => "string",
            "DateTime"       => "string",
            "DateTimeOffset" => "string",
            "DateOnly"       => "string",
            "TimeSpan"       => "string",
            "Object"         => "any",
            "Byte"           => "uint8",
            _                => type.Name
        };
    }

    private static string ToCamelCase(string name)
    {
        // Handle UPPER_CASE names like MUTED_BY_SERVER → mutedByServer
        if (name.Contains('_') || name == name.ToUpperInvariant())
        {
            var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
            return string.Concat(parts.Select((p, i) =>
                i == 0
                    ? p.ToLowerInvariant()
                    : char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));
        }

        // PascalCase → camelCase
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    // --- Original type system for contract hash computation (must not change!) ---

    private static string GetCanonicalTypeName(Type type)
    {
        if (type.IsGenericType)
        {
            var def  = type.GetGenericTypeDefinition();
            var args = string.Join(",", type.GetGenericArguments().Select(GetCanonicalTypeName));

            if (def == typeof(Nullable<>))
                return $"{args}?";
            if (def == typeof(List<>))
                return $"List<{args}>";
            if (def == typeof(IReadOnlyList<>))
                return $"List<{args}>";

            return $"{def.Name}<{args}>";
        }

        return type.Name switch
        {
            "String"         => "string",
            "Int32"          => "int",
            "Int64"          => "long",
            "Boolean"        => "bool",
            "Guid"           => "Guid",
            "DateTime"       => "DateTime",
            "DateTimeOffset" => "DateTimeOffset",
            _                => type.Name
        };
    }

    private static Type? GetInnerType(Type type)
    {
        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            if (def == typeof(List<>) || def == typeof(IReadOnlyList<>) || def == typeof(Nullable<>))
                return type.GetGenericArguments()[0];
        }
        return type.IsArray ? type.GetElementType() : null;
    }

    private static bool IsSimpleType(Type type) =>
        type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(Guid) ||
        type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(decimal);

    private static List<(Type Type, BotInterfaceAttribute Attr, BotInterfaceDeprecatedAttribute? Deprecated)> DiscoverInterfaces()
    {
        var result = new List<(Type, BotInterfaceAttribute, BotInterfaceDeprecatedAttribute?)>();

        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
           .Where(a => a.FullName?.StartsWith("Argon") == true);

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (!typeof(IBotInterface).IsAssignableFrom(type) || type.IsAbstract || type.IsInterface)
                    continue;

                var attr = type.GetCustomAttribute<BotInterfaceAttribute>();
                if (attr is null)
                    continue;

                var deprecated = type.GetCustomAttribute<BotInterfaceDeprecatedAttribute>();
                result.Add((type, attr, deprecated));
            }
        }

        return result;
    }
}

public sealed record ContractMismatch(
    string InterfaceName,
    string DeclaredHash,
    string ComputedHash);

public sealed record InterfaceManifest(
    string             Name,
    int                Version,
    string             ComputedHash,
    string?            DeclaredHash,
    bool               IsStable,
    bool               IsDeprecated,
    string?            Description,
    List<RouteManifest> Routes);

public sealed record RouteManifest(
    string               Method,
    string               Path,
    string?              Description,
    string?              RequestTypeName,
    string?              ResponseTypeName,
    List<TypeProperty>?  RequestTypeShape,
    List<TypeProperty>?  ResponseTypeShape,
    string?              Permission,
    bool                 IsPrivileged,
    List<ErrorManifest>? Errors);

public sealed record TypeProperty(
    string              Name,
    string              Type,
    bool                IsArray,
    bool                IsNullable,
    bool                IsCircular,
    List<TypeProperty>? Properties,
    string[]?           EnumValues = null,
    bool                IsUnion = false,
    string?             DiscriminatorField = null,
    string?             DiscriminatorType = null,
    List<UnionVariant>? Variants = null);

public sealed record UnionVariant(
    string             Name,
    string?            DiscriminatorValue,
    List<TypeProperty> Properties);

public sealed record DocsManifest(
    List<InterfaceManifest> Interfaces,
    List<IntentManifest>    Intents,
    List<EventManifest>     Events,
    List<RateLimitManifest> RateLimits);

public sealed record IntentManifest(
    string   Name,
    int      Bit,
    long     Value,
    bool     IsPrivileged,
    string[] Events);

public sealed record EventManifest(
    string                    Name,
    string?                   Intent,
    string?                   Description,
    string                    Category,
    string?                   PayloadTypeName = null,
    List<TypeProperty>?       PayloadTypeShape = null,
    List<EventPayloadVariant>? PayloadVariants = null);

public sealed record EventPayloadVariant(
    string             TypeName,
    List<TypeProperty>  Shape);

public sealed record RateLimitManifest(
    string InterfaceName,
    int    TokenLimit,
    int    TokensPerPeriod,
    string ReplenishmentPeriod);

public sealed record ErrorManifest(
    int    Status,
    string Code,
    string Description);
