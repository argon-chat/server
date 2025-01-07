namespace Argon.Services;

using MessagePack.Resolvers;
using Orleans.Serialization;

public static class MessagePackFeature
{
    public static void UseMessagePack(this WebApplicationBuilder builder)
    {
        var options = MessagePackSerializerOptions.Standard
           .WithResolver(CompositeResolver.Create(
                DynamicEnumAsStringResolver.Instance,
                EitherFormatterResolver.Instance,
                ArgonEventResolver.Instance));
        MessagePackSerializer.DefaultOptions = options;
        builder.Services.AddSingleton(options);
        builder.Services.AddSerializer(x =>
        {
            x.AddMessagePackSerializer(null, null, options);
        });
    }
}