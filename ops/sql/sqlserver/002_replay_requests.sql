IF SCHEMA_ID(N'lawwatcher') IS NULL
BEGIN
    EXEC(N'CREATE SCHEMA [lawwatcher]');
END
GO

IF OBJECT_ID(N'lawwatcher.replay_requests', N'U') IS NULL
BEGIN
    CREATE TABLE [lawwatcher].[replay_requests]
    (
        [replay_request_id] UNIQUEIDENTIFIER NOT NULL,
        [scope] NVARCHAR(200) NOT NULL,
        [status] NVARCHAR(32) NOT NULL,
        [requested_by] NVARCHAR(200) NOT NULL,
        [requested_at_utc] DATETIME2(7) NOT NULL,
        [started_at_utc] DATETIME2(7) NULL,
        [completed_at_utc] DATETIME2(7) NULL,
        CONSTRAINT [PK_replay_requests] PRIMARY KEY CLUSTERED ([replay_request_id] ASC)
    );

    CREATE INDEX [IX_replay_requests_status_requested_at_utc]
        ON [lawwatcher].[replay_requests] ([status] ASC, [requested_at_utc] ASC, [scope] ASC);
END
GO
