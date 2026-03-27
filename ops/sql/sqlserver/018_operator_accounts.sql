IF SCHEMA_ID(N'lawwatcher') IS NULL
BEGIN
    EXEC(N'CREATE SCHEMA [lawwatcher]');
END
GO

IF OBJECT_ID(N'lawwatcher.operator_accounts', N'U') IS NULL
BEGIN
    CREATE TABLE [lawwatcher].[operator_accounts]
    (
        [operator_id] UNIQUEIDENTIFIER NOT NULL,
        [email] NVARCHAR(320) NOT NULL,
        [display_name] NVARCHAR(300) NOT NULL,
        [password_hash] NVARCHAR(800) NOT NULL,
        [permissions_json] NVARCHAR(MAX) NOT NULL,
        [is_active] BIT NOT NULL,
        [registered_at_utc] DATETIME2(7) NOT NULL,
        [updated_at_utc] DATETIME2(7) NOT NULL,
        CONSTRAINT [PK_operator_accounts] PRIMARY KEY CLUSTERED ([operator_id] ASC)
    );

    CREATE UNIQUE INDEX [UX_operator_accounts_email]
        ON [lawwatcher].[operator_accounts] ([email] ASC);

    CREATE INDEX [IX_operator_accounts_display_name]
        ON [lawwatcher].[operator_accounts] ([display_name] ASC);
END
GO
