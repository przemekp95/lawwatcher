IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'lawwatcher')
BEGIN
    EXEC(N'CREATE SCHEMA [lawwatcher]');
END
GO

IF OBJECT_ID(N'[lawwatcher].[document_artifacts]', N'U') IS NULL
BEGIN
    CREATE TABLE [lawwatcher].[document_artifacts]
    (
        [artifact_id] UNIQUEIDENTIFIER NOT NULL,
        [owner_type] NVARCHAR(32) NOT NULL,
        [owner_id] UNIQUEIDENTIFIER NOT NULL,
        [source_kind] NVARCHAR(64) NOT NULL,
        [source_bucket] NVARCHAR(128) NOT NULL,
        [source_object_key] NVARCHAR(512) NOT NULL,
        [source_content_type] NVARCHAR(128) NOT NULL,
        [derived_kind] NVARCHAR(64) NOT NULL,
        [derived_bucket] NVARCHAR(128) NOT NULL,
        [derived_object_key] NVARCHAR(512) NOT NULL,
        [derived_content_type] NVARCHAR(128) NOT NULL,
        [extracted_text] NVARCHAR(MAX) NOT NULL,
        [created_at_utc] DATETIMEOFFSET(7) NOT NULL,
        CONSTRAINT [PK_document_artifacts] PRIMARY KEY CLUSTERED ([artifact_id])
    );
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'UX_document_artifacts_source_bucket_object_key'
      AND object_id = OBJECT_ID(N'[lawwatcher].[document_artifacts]')
)
BEGIN
    CREATE UNIQUE INDEX [UX_document_artifacts_source_bucket_object_key]
        ON [lawwatcher].[document_artifacts] ([source_bucket], [source_object_key]);
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_document_artifacts_created_at_utc'
      AND object_id = OBJECT_ID(N'[lawwatcher].[document_artifacts]')
)
BEGIN
    CREATE INDEX [IX_document_artifacts_created_at_utc]
        ON [lawwatcher].[document_artifacts] ([created_at_utc]);
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_document_artifacts_owner_type_owner_id'
      AND object_id = OBJECT_ID(N'[lawwatcher].[document_artifacts]')
)
BEGIN
    CREATE INDEX [IX_document_artifacts_owner_type_owner_id]
        ON [lawwatcher].[document_artifacts] ([owner_type], [owner_id]);
END
GO
