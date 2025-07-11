namespace Argon.Entities;

using Cassandra.Configuration;
using Cassandra.Core;
using Cassandra.Features.Messages;
using Cassandra.Mapping;

public class ArgonCassandraDbContext(CassandraConfiguration config, IServiceProvider serviceProvider, ILogger<ArgonCassandraDbContext> logger) 
    : CassandraDbContext(config, serviceProvider, logger)
{
    public CassandraDbSet<ArgonMessage>              ArgonMessages              => Set<ArgonMessage>();
    public CassandraDbSet<ArgonMessageDeduplication> ArgonMessagesDeduplication => Set<ArgonMessageDeduplication>();
    public CassandraDbSet<ArgonChannelMetadata>      ArgonChannelMetadata       => Set<ArgonChannelMetadata>();


    protected override void OnConfigureModels(IEntityMetadataContext metadataContext)
    {
        metadataContext.ForTable<ArgonMessage>()
           .WithClusteringKey(x => x.MessageId, 0)
           .WithPartitionKey(x => x.ServerId, 0)
           .WithPartitionKey(x => x.ChannelId, 1)
           .WithProperty(x => x.Entities).WithConverter<MessageEntityConverter>()
           .WithProperty(x => x.CreatedAt).WithConverter<DateTimeConverter>()
           .WithProperty(x => x.DeletedAt).WithConverter<DateTimeNullableConverter>()
           .WithProperty(x => x.UpdatedAt).WithConverter<DateTimeConverter>();

        metadataContext.ForTable<ArgonMessageDeduplication>()
           .WithClusteringKey(x => x.RandomId, 0)
           .WithPartitionKey(x => x.ServerId, 0)
           .WithPartitionKey(x => x.ChannelId, 1)
           .WithProperty(x => x.MessageId);

        metadataContext.ForTable<ArgonChannelMetadata>()
           .WithPartitionKey(x => x.ServerId, 0)
           .WithPartitionKey(x => x.ChannelId, 1)
           .WithProperty(x => x.LastMessageId);
    }
}