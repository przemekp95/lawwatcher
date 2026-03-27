IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'lawwatcher')
BEGIN
    EXEC(N'CREATE SCHEMA [lawwatcher]');
END
GO

IF OBJECT_ID(N'[lawwatcher].[search_documents]', N'U') IS NULL
BEGIN
    CREATE TABLE [lawwatcher].[search_documents]
    (
        [document_id] NVARCHAR(200) NOT NULL,
        [title] NVARCHAR(400) NOT NULL,
        [kind] NVARCHAR(32) NOT NULL,
        [snippet] NVARCHAR(MAX) NOT NULL,
        [keywords_json] NVARCHAR(MAX) NOT NULL,
        CONSTRAINT [PK_search_documents] PRIMARY KEY CLUSTERED ([document_id])
    );
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_search_documents_kind_title'
      AND object_id = OBJECT_ID(N'[lawwatcher].[search_documents]')
)
BEGIN
    CREATE INDEX [IX_search_documents_kind_title]
        ON [lawwatcher].[search_documents] ([kind], [title]);
END
GO
