IF SCHEMA_ID(N'lawwatcher') IS NULL
BEGIN
    EXEC(N'CREATE SCHEMA [lawwatcher]');
END
GO

IF OBJECT_ID(N'lawwatcher.alert_notification_dispatches', N'U') IS NULL
BEGIN
    CREATE TABLE [lawwatcher].[alert_notification_dispatches]
    (
        [alert_id] UNIQUEIDENTIFIER NOT NULL,
        [subscription_id] UNIQUEIDENTIFIER NOT NULL,
        [profile_name] NVARCHAR(300) NOT NULL,
        [bill_title] NVARCHAR(500) NOT NULL,
        [channel] NVARCHAR(64) NOT NULL,
        [recipient] NVARCHAR(500) NOT NULL,
        [dispatched_at_utc] DATETIME2(7) NOT NULL,
        CONSTRAINT [PK_alert_notification_dispatches] PRIMARY KEY CLUSTERED ([alert_id] ASC, [subscription_id] ASC)
    );

    CREATE INDEX [IX_alert_notification_dispatches_dispatched_at_utc]
        ON [lawwatcher].[alert_notification_dispatches] ([dispatched_at_utc] DESC);
END
GO
