IF SCHEMA_ID(N'lawwatcher') IS NULL
BEGIN
    EXEC(N'CREATE SCHEMA [lawwatcher]');
END
GO

IF OBJECT_ID(N'lawwatcher.api_clients', N'U') IS NULL
BEGIN
    CREATE TABLE [lawwatcher].[api_clients]
    (
        [client_id] UNIQUEIDENTIFIER NOT NULL,
        [name] NVARCHAR(300) NOT NULL,
        [client_identifier] NVARCHAR(200) NOT NULL,
        [token_fingerprint] NVARCHAR(300) NOT NULL,
        [scopes_json] NVARCHAR(MAX) NOT NULL,
        [is_active] BIT NOT NULL,
        [registered_at_utc] DATETIME2(7) NOT NULL,
        [updated_at_utc] DATETIME2(7) NOT NULL,
        CONSTRAINT [PK_api_clients] PRIMARY KEY CLUSTERED ([client_id] ASC)
    );

    CREATE UNIQUE INDEX [UX_api_clients_client_identifier]
        ON [lawwatcher].[api_clients] ([client_identifier] ASC);

    CREATE UNIQUE INDEX [UX_api_clients_token_fingerprint]
        ON [lawwatcher].[api_clients] ([token_fingerprint] ASC);

    CREATE INDEX [IX_api_clients_name]
        ON [lawwatcher].[api_clients] ([name] ASC);
END
GO
