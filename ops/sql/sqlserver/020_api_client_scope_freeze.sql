IF OBJECT_ID(N'lawwatcher.api_clients', N'U') IS NOT NULL
BEGIN
    UPDATE api_clients
    SET [scopes_json] = normalized.[scopes_json]
    FROM [lawwatcher].[api_clients] AS api_clients
    CROSS APPLY
    (
        SELECT
            COALESCE(
                N'[' +
                STRING_AGG(
                    N'"' + STRING_ESCAPE(normalized_scopes.[scope_value], 'json') + N'"',
                    N',') WITHIN GROUP (ORDER BY normalized_scopes.[scope_value]) +
                N']',
                N'[]') AS [scopes_json]
        FROM
        (
            SELECT DISTINCT
                CASE CAST(scopes.[value] AS NVARCHAR(200))
                    WHEN N'search:read' THEN N'integration:read'
                    WHEN N'alerts:read' THEN N'integration:read'
                    ELSE CAST(scopes.[value] AS NVARCHAR(200))
                END AS [scope_value]
            FROM OPENJSON(api_clients.[scopes_json]) AS scopes
        ) AS normalized_scopes
    ) AS normalized
    WHERE ISJSON(api_clients.[scopes_json]) = 1;
END
GO

IF OBJECT_ID(N'lawwatcher.event_store', N'U') IS NOT NULL
BEGIN
    UPDATE event_store
    SET [payload] = JSON_MODIFY(event_store.[payload], '$.scopes', JSON_QUERY(normalized.[scopes_json]))
    FROM [lawwatcher].[event_store] AS event_store
    CROSS APPLY
    (
        SELECT
            COALESCE(
                N'[' +
                STRING_AGG(
                    N'"' + STRING_ESCAPE(normalized_scopes.[scope_value], 'json') + N'"',
                    N',') WITHIN GROUP (ORDER BY normalized_scopes.[scope_value]) +
                N']',
                N'[]') AS [scopes_json]
        FROM
        (
            SELECT DISTINCT
                CASE CAST(scopes.[value] AS NVARCHAR(200))
                    WHEN N'search:read' THEN N'integration:read'
                    WHEN N'alerts:read' THEN N'integration:read'
                    ELSE CAST(scopes.[value] AS NVARCHAR(200))
                END AS [scope_value]
            FROM OPENJSON(JSON_QUERY(event_store.[payload], '$.scopes')) AS scopes
        ) AS normalized_scopes
    ) AS normalized
    WHERE event_store.[stream_id] LIKE N'api-client:%'
      AND ISJSON(event_store.[payload]) = 1
      AND JSON_QUERY(event_store.[payload], '$.scopes') IS NOT NULL;
END
GO
