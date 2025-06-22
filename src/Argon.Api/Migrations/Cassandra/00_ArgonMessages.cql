CREATE TABLE IF NOT EXISTS argon_messages (
    server_id UUID,
    channel_id UUID,
    message_id BIGINT,
    author_id UUID,
    reply BIGINT,
    text TEXT,
    entities TEXT,
    created_at TIMESTAMP,
    is_deleted BOOLEAN,
    deleted_at TIMESTAMP,
    updated_at TIMESTAMP,
    PRIMARY KEY ((server_id, channel_id), message_id)
) WITH CLUSTERING ORDER BY (message_id ASC)