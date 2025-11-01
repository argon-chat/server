namespace Argon.Entities;

using Argon.Api.Features.CoreLogic.Messages;
using Cassandra.Configuration;
using Cassandra.Core;
using Cassandra.Mapping;

public class ArgonCassandraDbContext(CassandraConfiguration config, IServiceProvider serviceProvider, ILogger<ArgonCassandraDbContext> logger)
    : CassandraDbContext(config, serviceProvider, logger)
{
    public CassandraDbSet<ArgonMessageEntity>         ArgonMessages              => Set<ArgonMessageEntity>();
    public CassandraDbSet<ArgonMessageDeduplication>  ArgonMessagesDeduplication => Set<ArgonMessageDeduplication>();


    protected override void OnConfigureModels(IEntityMetadataContext metadataContext)
    {
        metadataContext.ForTable<ArgonMessageEntity>("argonmessage")
           .WithClusteringKey(x => x.MessageId, 0)
           .WithPartitionKey(x => x.SpaceId, 0)
           .WithPartitionKey(x => x.ChannelId, 1)
           .WithProperty(x => x.Entities).WithConverter<MessageEntityConverter>()
           .WithProperty(x => x.CreatedAt).WithConverter<DateTimeConverter>()
           .WithProperty(x => x.DeletedAt).WithConverter<DateTimeNullableConverter>()
           .WithProperty(x => x.UpdatedAt).WithConverter<DateTimeConverter>();

        metadataContext.ForTable<ArgonMessageDeduplication>()
           .WithClusteringKey(x => x.RandomId, 0)
           .WithPartitionKey(x => x.spaceId, 0)
           .WithPartitionKey(x => x.ChannelId, 1)
           .WithProperty(x => x.MessageId);
    }
}