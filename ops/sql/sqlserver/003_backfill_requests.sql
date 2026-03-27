IF SCHEMA_ID(N'lawwatcher') IS NULL
BEGIN
    EXEC(N'CREATE SCHEMA [lawwatcher]');
END
GO

IF OBJECT_ID(N'lawwatcher.backfill_requests', N'U') IS NULL
BEGIN
    CREATE TABLE [lawwatcher].[backfill_requests]
    (
        [backfill_request_id] UNIQUEIDENTIFIER NOT NULL,
        [source] NVARCHAR(200) NOT NULL,
        [scope] NVARCHAR(200) NOT NULL,
        [status] NVARCHAR(32) NOT NULL,
        [requested_by] NVARCHAR(200) NOT NULL,
        [requested_from] DATE NOT NULL,
        [requested_to] DATE NULL,
        [requested_at_utc] DATETIME2(7) NOT NULL,
        [started_at_utc] DATETIME2(7) NULL,
        [completed_at_utc] DATETIME2(7) NULL,
        CONSTRAINT [PK_backfill_requests] PRIMARY KEY CLUSTERED ([backfill_request_id] ASC)
    );

    CREATE INDEX [IX_backfill_requests_status_requested_at_utc]
        ON [lawwatcher].[backfill_requests] ([status] ASC, [requested_at_utc] ASC, [source] ASC, [scope] ASC);
END
GO
