CREATE TABLE IF NOT EXISTS channel_metadata (
    server_id UUID,
    channel_id UUID,
    last_message_id BIGINT,
    PRIMARY KEY ((server_id, channel_id))
)