CREATE TABLE IF NOT EXISTS ArgonMessageDeduplication (
    ServerId UUID,
    ChannelId UUID,
    RandomId BIGINT,
    MessageId BIGINT,
    PRIMARY KEY ((ServerId, ChannelId), RandomId)
)