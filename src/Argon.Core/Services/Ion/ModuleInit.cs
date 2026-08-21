#pragma warning disable CA2255

namespace Argon.Services.Ion;

using System.Runtime.CompilerServices;
using Api.Features.Utils;

public static class ModuleInitIon
{
    [ModuleInitializer]
    internal static void Init()
        => JsonConvert.DefaultSettings = () => new JsonSerializerSettings()
        {
            Converters =
            {
                new UlongEnumConverter<ArgonEntitlement>()
            }
        };
}