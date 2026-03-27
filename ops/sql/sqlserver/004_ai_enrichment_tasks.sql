IF SCHEMA_ID(N'lawwatcher') IS NULL
BEGIN
    EXEC(N'CREATE SCHEMA [lawwatcher]');
END
GO

IF OBJECT_ID(N'lawwatcher.ai_enrichment_tasks', N'U') IS NULL
BEGIN
    CREATE TABLE [lawwatcher].[ai_enrichment_tasks]
    (
        [task_id] UNIQUEIDENTIFIER NOT NULL,
        [kind] NVARCHAR(100) NOT NULL,
        [subject_type] NVARCHAR(100) NOT NULL,
        [subject_id] UNIQUEIDENTIFIER NOT NULL,
        [subject_title] NVARCHAR(300) NOT NULL,
        [status] NVARCHAR(32) NOT NULL,
        [model] NVARCHAR(200) NULL,
        [content] NVARCHAR(MAX) NULL,
        [error] NVARCHAR(MAX) NULL,
        [citations_json] NVARCHAR(MAX) NOT NULL,
        [requested_at_utc] DATETIME2(7) NOT NULL,
        [started_at_utc] DATETIME2(7) NULL,
        [completed_at_utc] DATETIME2(7) NULL,
        [failed_at_utc] DATETIME2(7) NULL,
        CONSTRAINT [PK_ai_enrichment_tasks] PRIMARY KEY CLUSTERED ([task_id] ASC)
    );

    CREATE INDEX [IX_ai_enrichment_tasks_status_requested_at_utc]
        ON [lawwatcher].[ai_enrichment_tasks] ([status] ASC, [requested_at_utc] ASC, [subject_title] ASC);
END
GO
