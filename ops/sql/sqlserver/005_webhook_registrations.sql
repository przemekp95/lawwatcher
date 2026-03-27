IF SCHEMA_ID(N'lawwatcher') IS NULL
BEGIN
    EXEC(N'CREATE SCHEMA [lawwatcher]');
END
GO

IF OBJECT_ID(N'lawwatcher.webhook_registrations', N'U') IS NULL
BEGIN
    CREATE TABLE [lawwatcher].[webhook_registrations]
    (
        [registration_id] UNIQUEIDENTIFIER NOT NULL,
        [name] NVARCHAR(300) NOT NULL,
        [callback_url] NVARCHAR(1000) NOT NULL,
        [event_types_json] NVARCHAR(MAX) NOT NULL,
        [is_active] BIT NOT NULL,
        [updated_at_utc] DATETIME2(7) NOT NULL,
        CONSTRAINT [PK_webhook_registrations] PRIMARY KEY CLUSTERED ([registration_id] ASC)
    );

    CREATE INDEX [IX_webhook_registrations_name]
        ON [lawwatcher].[webhook_registrations] ([name] ASC);
END
GO
