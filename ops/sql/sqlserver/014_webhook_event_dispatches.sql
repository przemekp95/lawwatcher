IF SCHEMA_ID(N'lawwatcher') IS NULL
BEGIN
    EXEC(N'CREATE SCHEMA [lawwatcher]');
END
GO

IF OBJECT_ID(N'lawwatcher.webhook_event_dispatches', N'U') IS NULL
BEGIN
    CREATE TABLE [lawwatcher].[webhook_event_dispatches]
    (
        [alert_id] UNIQUEIDENTIFIER NOT NULL,
        [registration_id] UNIQUEIDENTIFIER NOT NULL,
        [event_type] NVARCHAR(128) NOT NULL,
        [callback_url] NVARCHAR(1000) NOT NULL,
        [dispatched_at_utc] DATETIME2(7) NOT NULL,
        CONSTRAINT [PK_webhook_event_dispatches] PRIMARY KEY CLUSTERED ([alert_id] ASC, [registration_id] ASC, [event_type] ASC)
    );

    CREATE INDEX [IX_webhook_event_dispatches_dispatched_at_utc]
        ON [lawwatcher].[webhook_event_dispatches] ([dispatched_at_utc] DESC);
END
GO
