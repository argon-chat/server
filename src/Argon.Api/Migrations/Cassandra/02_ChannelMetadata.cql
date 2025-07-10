CREATE TABLE IF NOT EXISTS ArgonChannelMetadata (
    ServerId UUID,
    ChannelId UUID,
    LastMessageId BIGINT,
    PRIMARY KEY ((ServerId, ChannelId))
)