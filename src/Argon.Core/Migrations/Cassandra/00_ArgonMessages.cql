CREATE TABLE IF NOT EXISTS ArgonMessage (
    MessageId BIGINT,
    ServerId UUID,
    ChannelId UUID,
    CreatorId UUID,
    Reply BIGINT,
    Text TEXT,
    Entities TEXT,
    CreatedAt TIMESTAMP,
    IsDeleted BOOLEAN,
    DeletedAt TIMESTAMP,
    UpdatedAt TIMESTAMP,
    PRIMARY KEY ((ServerId, ChannelId), MessageId)
) WITH CLUSTERING ORDER BY (MessageId ASC)