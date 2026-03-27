SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID(N'lawwatcher.published_acts', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'lawwatcher.published_acts', N'eli_hash') IS NULL
    BEGIN
        ALTER TABLE [lawwatcher].[published_acts]
        ADD [eli_hash] AS CONVERT(VARBINARY(32), HASHBYTES('SHA2_256', UPPER(CONVERT(NVARCHAR(1000), [eli])))) PERSISTED;
    END

    IF EXISTS (
        SELECT 1
        FROM sys.indexes
        WHERE [name] = N'UX_published_acts_eli'
          AND [object_id] = OBJECT_ID(N'lawwatcher.published_acts'))
    BEGIN
        DROP INDEX [UX_published_acts_eli] ON [lawwatcher].[published_acts];
    END

    IF NOT EXISTS (
        SELECT 1
        FROM sys.indexes
        WHERE [name] = N'UX_published_acts_eli_hash'
          AND [object_id] = OBJECT_ID(N'lawwatcher.published_acts'))
    BEGIN
        CREATE UNIQUE INDEX [UX_published_acts_eli_hash]
            ON [lawwatcher].[published_acts] ([eli_hash] ASC);
    END
END
GO
