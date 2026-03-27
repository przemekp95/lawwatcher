IF COL_LENGTH(N'[lawwatcher].[search_documents]', N'indexed_at_utc') IS NULL
BEGIN
    ALTER TABLE [lawwatcher].[search_documents]
        ADD [indexed_at_utc] DATETIMEOFFSET(7) NOT NULL
            CONSTRAINT [DF_search_documents_indexed_at_utc] DEFAULT SYSUTCDATETIME();
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_search_documents_indexed_at_utc'
      AND object_id = OBJECT_ID(N'[lawwatcher].[search_documents]')
)
BEGIN
    CREATE INDEX [IX_search_documents_indexed_at_utc]
        ON [lawwatcher].[search_documents] ([indexed_at_utc]);
END
GO
