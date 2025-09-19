CREATE TABLE FileReferences (
    FileId UUID,
    ReferenceType TEXT,
    ReferenceId UUID,
    CreatedAt TIMESTAMP,
    PRIMARY KEY (FileId, ReferenceType, ReferenceId)
)