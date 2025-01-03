namespace Argon.Shared;

using System.Runtime.CompilerServices;
using Streaming;
using MessagePack.Resolvers;

public static class MsgPackIniter
{
    [ModuleInitializer]
    public static void Init()
    {
        var options = MessagePackSerializerOptions.Standard
           .WithResolver(CompositeResolver.Create(
                DynamicEnumAsStringResolver.Instance,
                EitherFormatterResolver.Instance,
                ArgonEventResolver.Instance));
        MessagePackSerializer.DefaultOptions = options;
    }
}