IF SCHEMA_ID(N'lawwatcher') IS NULL
BEGIN
    EXEC(N'CREATE SCHEMA [lawwatcher]');
END
GO

IF OBJECT_ID(N'lawwatcher.profile_subscriptions', N'U') IS NULL
BEGIN
    CREATE TABLE [lawwatcher].[profile_subscriptions]
    (
        [subscription_id] UNIQUEIDENTIFIER NOT NULL,
        [profile_id] UNIQUEIDENTIFIER NOT NULL,
        [profile_name] NVARCHAR(300) NOT NULL,
        [subscriber] NVARCHAR(500) NOT NULL,
        [channel] NVARCHAR(64) NOT NULL,
        [alert_policy] NVARCHAR(64) NOT NULL,
        [digest_interval_minutes] INT NULL,
        [updated_at_utc] DATETIME2(7) NOT NULL,
        CONSTRAINT [PK_profile_subscriptions] PRIMARY KEY CLUSTERED ([subscription_id] ASC)
    );

    CREATE INDEX [IX_profile_subscriptions_profile_id_channel]
        ON [lawwatcher].[profile_subscriptions] ([profile_id] ASC, [channel] ASC);

    CREATE INDEX [IX_profile_subscriptions_subscriber]
        ON [lawwatcher].[profile_subscriptions] ([subscriber] ASC);
END
GO
