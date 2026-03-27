IF SCHEMA_ID(N'lawwatcher') IS NULL
BEGIN
    EXEC(N'CREATE SCHEMA [lawwatcher]');
END
GO

IF OBJECT_ID(N'lawwatcher.event_store', N'U') IS NULL
BEGIN
    CREATE TABLE [lawwatcher].[event_store]
    (
        [event_id] UNIQUEIDENTIFIER NOT NULL CONSTRAINT [DF_event_store_event_id] DEFAULT NEWSEQUENTIALID(),
        [stream_id] NVARCHAR(200) NOT NULL,
        [stream_type] NVARCHAR(200) NOT NULL,
        [stream_version] BIGINT NOT NULL,
        [event_type] NVARCHAR(200) NOT NULL,
        [event_schema_version] INT NOT NULL,
        [payload] NVARCHAR(MAX) NOT NULL,
        [metadata] NVARCHAR(MAX) NULL,
        [occurred_at_utc] DATETIME2(7) NOT NULL,
        [recorded_at_utc] DATETIME2(7) NOT NULL CONSTRAINT [DF_event_store_recorded_at_utc] DEFAULT SYSUTCDATETIME(),
        [correlation_id] UNIQUEIDENTIFIER NULL,
        [causation_id] UNIQUEIDENTIFIER NULL,
        [tenant_id] NVARCHAR(100) NULL,
        CONSTRAINT [PK_event_store] PRIMARY KEY CLUSTERED ([event_id] ASC)
    );

    CREATE UNIQUE INDEX [UX_event_store_stream_id_version]
        ON [lawwatcher].[event_store] ([stream_id] ASC, [stream_version] ASC);

    CREATE INDEX [IX_event_store_stream_type_stream_id]
        ON [lawwatcher].[event_store] ([stream_type] ASC, [stream_id] ASC);
END
GO

IF OBJECT_ID(N'lawwatcher.stream_snapshots', N'U') IS NULL
BEGIN
    CREATE TABLE [lawwatcher].[stream_snapshots]
    (
        [snapshot_id] UNIQUEIDENTIFIER NOT NULL CONSTRAINT [DF_stream_snapshots_snapshot_id] DEFAULT NEWSEQUENTIALID(),
        [stream_id] NVARCHAR(200) NOT NULL,
        [stream_type] NVARCHAR(200) NOT NULL,
        [stream_version] BIGINT NOT NULL,
        [snapshot_type] NVARCHAR(200) NOT NULL,
        [payload] NVARCHAR(MAX) NOT NULL,
        [metadata] NVARCHAR(MAX) NULL,
        [created_at_utc] DATETIME2(7) NOT NULL CONSTRAINT [DF_stream_snapshots_created_at_utc] DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_stream_snapshots] PRIMARY KEY CLUSTERED ([snapshot_id] ASC)
    );

    CREATE UNIQUE INDEX [UX_stream_snapshots_stream_id_version]
        ON [lawwatcher].[stream_snapshots] ([stream_id] ASC, [stream_version] ASC);
END
GO

IF OBJECT_ID(N'lawwatcher.outbox', N'U') IS NULL
BEGIN
    CREATE TABLE [lawwatcher].[outbox]
    (
        [outbox_message_id] UNIQUEIDENTIFIER NOT NULL CONSTRAINT [DF_outbox_outbox_message_id] DEFAULT NEWSEQUENTIALID(),
        [message_type] NVARCHAR(200) NOT NULL,
        [message_version] INT NOT NULL,
        [payload] NVARCHAR(MAX) NOT NULL,
        [metadata] NVARCHAR(MAX) NULL,
        [status] NVARCHAR(32) NOT NULL,
        [attempt_count] INT NOT NULL CONSTRAINT [DF_outbox_attempt_count] DEFAULT 0,
        [next_attempt_at_utc] DATETIME2(7) NULL,
        [published_at_utc] DATETIME2(7) NULL,
        [created_at_utc] DATETIME2(7) NOT NULL CONSTRAINT [DF_outbox_created_at_utc] DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_outbox] PRIMARY KEY CLUSTERED ([outbox_message_id] ASC)
    );

    CREATE INDEX [IX_outbox_status_next_attempt]
        ON [lawwatcher].[outbox] ([status] ASC, [next_attempt_at_utc] ASC);
END
GO

IF OBJECT_ID(N'lawwatcher.inbox', N'U') IS NULL
BEGIN
    CREATE TABLE [lawwatcher].[inbox]
    (
        [inbox_message_id] UNIQUEIDENTIFIER NOT NULL CONSTRAINT [DF_inbox_inbox_message_id] DEFAULT NEWSEQUENTIALID(),
        [message_id] UNIQUEIDENTIFIER NOT NULL,
        [consumer_name] NVARCHAR(200) NOT NULL,
        [received_at_utc] DATETIME2(7) NOT NULL CONSTRAINT [DF_inbox_received_at_utc] DEFAULT SYSUTCDATETIME(),
        [processed_at_utc] DATETIME2(7) NULL,
        [status] NVARCHAR(32) NOT NULL,
        [metadata] NVARCHAR(MAX) NULL,
        CONSTRAINT [PK_inbox] PRIMARY KEY CLUSTERED ([inbox_message_id] ASC)
    );

    CREATE UNIQUE INDEX [UX_inbox_message_consumer]
        ON [lawwatcher].[inbox] ([message_id] ASC, [consumer_name] ASC);
END
GO

IF OBJECT_ID(N'lawwatcher.consumer_offsets', N'U') IS NULL
BEGIN
    CREATE TABLE [lawwatcher].[consumer_offsets]
    (
        [consumer_name] NVARCHAR(200) NOT NULL,
        [partition_key] NVARCHAR(200) NOT NULL,
        [offset_value] BIGINT NOT NULL,
        [updated_at_utc] DATETIME2(7) NOT NULL CONSTRAINT [DF_consumer_offsets_updated_at_utc] DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_consumer_offsets] PRIMARY KEY CLUSTERED ([consumer_name] ASC, [partition_key] ASC)
    );
END
GO
