namespace Argon.Services;

using MessagePack.Resolvers;
using Orleans.Serialization;

public static class MessagePackFeature
{
    public static void UseMessagePack(this WebApplicationBuilder builder)
        => builder.Services.UseOrleansMessagePack();

    public static void UseOrleansMessagePack(this IServiceCollection collection)
    {
        var options = MessagePackSerializerOptions.Standard
           .WithResolver(CompositeResolver.Create(
                DynamicEnumAsStringResolver.Instance,
                EitherFormatterResolver.Instance,
                ArgonEventResolver.Instance));
        MessagePackSerializer.DefaultOptions = options;
        collection.AddSingleton(options);
        collection.AddSerializer(x => {
            x.AddMessagePackSerializer(null, null, options);
        });
    }
}