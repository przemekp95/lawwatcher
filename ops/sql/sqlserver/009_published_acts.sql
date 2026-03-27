SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER ON
GO

IF SCHEMA_ID(N'lawwatcher') IS NULL
BEGIN
    EXEC(N'CREATE SCHEMA [lawwatcher]');
END
GO

IF OBJECT_ID(N'lawwatcher.published_acts', N'U') IS NULL
BEGIN
    CREATE TABLE [lawwatcher].[published_acts]
    (
        [act_id] UNIQUEIDENTIFIER NOT NULL,
        [bill_id] UNIQUEIDENTIFIER NOT NULL,
        [bill_title] NVARCHAR(500) NOT NULL,
        [bill_external_id] NVARCHAR(100) NOT NULL,
        [eli] NVARCHAR(1000) NOT NULL,
        [eli_hash] AS CONVERT(VARBINARY(32), HASHBYTES('SHA2_256', UPPER(CONVERT(NVARCHAR(1000), [eli])))) PERSISTED,
        [title] NVARCHAR(500) NOT NULL,
        [published_on] DATE NOT NULL,
        [effective_from] DATE NULL,
        [artifact_kinds_json] NVARCHAR(MAX) NOT NULL,
        [updated_at_utc] DATETIME2(7) NOT NULL,
        CONSTRAINT [PK_published_acts] PRIMARY KEY CLUSTERED ([act_id] ASC)
    );

    CREATE UNIQUE INDEX [UX_published_acts_eli_hash]
        ON [lawwatcher].[published_acts] ([eli_hash] ASC);

    CREATE UNIQUE INDEX [UX_published_acts_bill_id]
        ON [lawwatcher].[published_acts] ([bill_id] ASC);

    CREATE INDEX [IX_published_acts_published_on_title]
        ON [lawwatcher].[published_acts] ([published_on] DESC, [title] ASC);
END
GO
