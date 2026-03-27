IF SCHEMA_ID(N'lawwatcher') IS NULL
BEGIN
    EXEC(N'CREATE SCHEMA [lawwatcher]');
END
GO

IF OBJECT_ID(N'lawwatcher.imported_bills', N'U') IS NULL
BEGIN
    CREATE TABLE [lawwatcher].[imported_bills]
    (
        [bill_id] UNIQUEIDENTIFIER NOT NULL,
        [source_system] NVARCHAR(100) NOT NULL,
        [external_id] NVARCHAR(100) NOT NULL,
        [title] NVARCHAR(500) NOT NULL,
        [source_url] NVARCHAR(1000) NOT NULL,
        [submitted_on] DATE NOT NULL,
        [document_kinds_json] NVARCHAR(MAX) NOT NULL,
        [updated_at_utc] DATETIME2(7) NOT NULL,
        CONSTRAINT [PK_imported_bills] PRIMARY KEY CLUSTERED ([bill_id] ASC)
    );

    CREATE INDEX [IX_imported_bills_submitted_on_title]
        ON [lawwatcher].[imported_bills] ([submitted_on] DESC, [title] ASC);

    CREATE UNIQUE INDEX [UX_imported_bills_external_reference]
        ON [lawwatcher].[imported_bills] ([source_system] ASC, [external_id] ASC);
END
GO
