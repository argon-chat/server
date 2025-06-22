CREATE TABLE IF NOT EXISTS message_deduplication (
    server_id UUID,
    channel_id UUID,
    random_id BIGINT,
    message_id BIGINT,
    PRIMARY KEY ((server_id, channel_id), random_id)
)