IF COL_LENGTH(N'[lawwatcher].[search_documents]', N'keywords_text') IS NULL
BEGIN
    ALTER TABLE [lawwatcher].[search_documents]
        ADD [keywords_text] NVARCHAR(MAX) NOT NULL
            CONSTRAINT [DF_search_documents_keywords_text] DEFAULT N'';
END
GO

IF CONVERT(INT, ISNULL(SERVERPROPERTY('IsFullTextInstalled'), 0)) = 1
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = N'LawWatcherSearchCatalog')
    BEGIN
        EXEC(N'CREATE FULLTEXT CATALOG [LawWatcherSearchCatalog] AS DEFAULT;');
    END
END
GO

IF CONVERT(INT, ISNULL(SERVERPROPERTY('IsFullTextInstalled'), 0)) = 1
   AND NOT EXISTS
   (
       SELECT 1
       FROM sys.fulltext_indexes
       WHERE object_id = OBJECT_ID(N'[lawwatcher].[search_documents]')
   )
BEGIN
    EXEC(N'
        CREATE FULLTEXT INDEX ON [lawwatcher].[search_documents]
        (
            [title] LANGUAGE 0,
            [snippet] LANGUAGE 0,
            [keywords_text] LANGUAGE 0
        )
        KEY INDEX [PK_search_documents]
        WITH CHANGE_TRACKING AUTO;
    ');
END
GO
