IF SCHEMA_ID(N'lawwatcher') IS NULL
BEGIN
    EXEC(N'CREATE SCHEMA [lawwatcher]');
END
GO

IF OBJECT_ID(N'lawwatcher.bill_alert_pairs', N'U') IS NULL
BEGIN
    CREATE TABLE [lawwatcher].[bill_alert_pairs]
    (
        [alert_id] UNIQUEIDENTIFIER NOT NULL,
        [profile_id] UNIQUEIDENTIFIER NOT NULL,
        [bill_id] UNIQUEIDENTIFIER NOT NULL,
        [created_at_utc] DATETIME2(7) NOT NULL,
        CONSTRAINT [PK_bill_alert_pairs] PRIMARY KEY CLUSTERED ([alert_id] ASC)
    );

    CREATE UNIQUE INDEX [UX_bill_alert_pairs_profile_bill]
        ON [lawwatcher].[bill_alert_pairs] ([profile_id] ASC, [bill_id] ASC);
END
GO

IF OBJECT_ID(N'lawwatcher.bill_alerts', N'U') IS NULL
BEGIN
    CREATE TABLE [lawwatcher].[bill_alerts]
    (
        [alert_id] UNIQUEIDENTIFIER NOT NULL,
        [profile_id] UNIQUEIDENTIFIER NOT NULL,
        [profile_name] NVARCHAR(300) NOT NULL,
        [bill_id] UNIQUEIDENTIFIER NOT NULL,
        [bill_title] NVARCHAR(500) NOT NULL,
        [bill_external_id] NVARCHAR(100) NOT NULL,
        [bill_submitted_on] DATE NOT NULL,
        [alert_policy] NVARCHAR(64) NOT NULL,
        [matched_keywords_json] NVARCHAR(MAX) NOT NULL,
        [created_at_utc] DATETIME2(7) NOT NULL,
        CONSTRAINT [PK_bill_alerts] PRIMARY KEY CLUSTERED ([alert_id] ASC)
    );

    CREATE UNIQUE INDEX [UX_bill_alerts_profile_bill]
        ON [lawwatcher].[bill_alerts] ([profile_id] ASC, [bill_id] ASC);

    CREATE INDEX [IX_bill_alerts_bill_submitted_on_profile_name]
        ON [lawwatcher].[bill_alerts] ([bill_submitted_on] DESC, [profile_name] ASC);
END
GO
