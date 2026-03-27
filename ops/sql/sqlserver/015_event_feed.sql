IF SCHEMA_ID(N'lawwatcher') IS NULL
BEGIN
    EXEC(N'CREATE SCHEMA [lawwatcher]');
END
GO

IF OBJECT_ID(N'lawwatcher.event_feed', N'U') IS NULL
BEGIN
    CREATE TABLE [lawwatcher].[event_feed]
    (
        [event_id] NVARCHAR(200) NOT NULL,
        [type] NVARCHAR(128) NOT NULL,
        [subject_type] NVARCHAR(64) NOT NULL,
        [subject_id] NVARCHAR(200) NOT NULL,
        [title] NVARCHAR(500) NOT NULL,
        [summary] NVARCHAR(2000) NOT NULL,
        [occurred_at_utc] DATETIME2(7) NOT NULL,
        CONSTRAINT [PK_event_feed] PRIMARY KEY CLUSTERED ([event_id] ASC)
    );

    CREATE INDEX [IX_event_feed_occurred_at_utc]
        ON [lawwatcher].[event_feed] ([occurred_at_utc] DESC);
END
GO
