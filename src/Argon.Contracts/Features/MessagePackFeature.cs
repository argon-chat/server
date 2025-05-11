namespace Argon.Services;

using MessagePack.Resolvers;
using Orleans.Serialization;
using Streaming;

public static class MessagePackFeature
{
    public static void UseMessagePack(this WebApplicationBuilder builder)
        => builder.Services.UseOrleansMessagePack();

    public static void UseOrleansMessagePack(this IServiceCollection collection)
    {
        var options = MessagePackSerializerOptions.Standard
           .WithResolver(CompositeResolver.Create(
                [
                    new MessageEntityFormatter()
                ],
                [
                    DynamicEnumAsStringResolver.Instance,
                    EitherFormatterResolver.Instance,
                    ArgonEventResolver.Instance,
                    StandardResolver.Instance
                ]));

        MessagePackSerializer.DefaultOptions = options;

        collection.AddSingleton(options);

        collection.AddSerializer(x =>
        {
            x.AddMessagePackSerializer(null, null, options);
        });
    }
}