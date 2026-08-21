CREATE TABLE IF NOT EXISTS ArgonMessage (
    MessageId BIGINT,
    SpaceId UUID,
    ChannelId UUID,
    CreatorId UUID,
    Reply BIGINT,
    Text TEXT,
    Entities TEXT,
    CreatedAt TIMESTAMP,
    IsDeleted BOOLEAN,
    DeletedAt TIMESTAMP,
    UpdatedAt TIMESTAMP,
    PRIMARY KEY ((SpaceId, ChannelId), MessageId)
) WITH CLUSTERING ORDER BY (MessageId ASC)