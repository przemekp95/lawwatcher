IF OBJECT_ID(N'lawwatcher.monitoring_profile_projections', N'U') IS NULL
BEGIN
    CREATE TABLE [lawwatcher].[monitoring_profile_projections]
    (
        [profile_id] UNIQUEIDENTIFIER NOT NULL,
        [name] NVARCHAR(300) NOT NULL,
        [alert_policy] NVARCHAR(32) NOT NULL,
        [keywords_json] NVARCHAR(MAX) NOT NULL,
        [updated_at_utc] DATETIME2(7) NOT NULL,
        CONSTRAINT [PK_monitoring_profile_projections] PRIMARY KEY CLUSTERED ([profile_id] ASC)
    );

    CREATE INDEX [IX_monitoring_profile_projections_name]
        ON [lawwatcher].[monitoring_profile_projections] ([name] ASC);
END
GO
