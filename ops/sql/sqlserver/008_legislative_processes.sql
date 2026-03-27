IF SCHEMA_ID(N'lawwatcher') IS NULL
BEGIN
    EXEC(N'CREATE SCHEMA [lawwatcher]');
END
GO

IF OBJECT_ID(N'lawwatcher.legislative_processes', N'U') IS NULL
BEGIN
    CREATE TABLE [lawwatcher].[legislative_processes]
    (
        [process_id] UNIQUEIDENTIFIER NOT NULL,
        [bill_id] UNIQUEIDENTIFIER NOT NULL,
        [bill_title] NVARCHAR(500) NOT NULL,
        [bill_external_id] NVARCHAR(100) NOT NULL,
        [current_stage_code] NVARCHAR(100) NOT NULL,
        [current_stage_label] NVARCHAR(200) NOT NULL,
        [last_updated_on] DATE NOT NULL,
        [stages_json] NVARCHAR(MAX) NOT NULL,
        [updated_at_utc] DATETIME2(7) NOT NULL,
        CONSTRAINT [PK_legislative_processes] PRIMARY KEY CLUSTERED ([process_id] ASC)
    );

    CREATE UNIQUE INDEX [UX_legislative_processes_bill_id]
        ON [lawwatcher].[legislative_processes] ([bill_id] ASC);

    CREATE INDEX [IX_legislative_processes_last_updated_on_title]
        ON [lawwatcher].[legislative_processes] ([last_updated_on] DESC, [bill_title] ASC);
END
GO
