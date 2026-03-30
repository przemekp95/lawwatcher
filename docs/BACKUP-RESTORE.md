# Backup And Restore

## Backup Boundary

The supported durable data boundary for the product is:

- `SQL Server`
- `MinIO`

`RabbitMQ` is not a backup boundary. It is treated as reconstructible transport state.

## Backup Expectations

- back up the `LawWatcher` SQL database on a regular schedule
- back up the MinIO buckets used for document and artifact storage
- keep SQL and MinIO backups aligned to the same recovery window
- version the production env file outside the repo with your secret management process

## Restore Expectations

1. Restore `SQL Server`.
2. Restore `MinIO`.
3. Bring the production bundle back with the same release tag or the documented upgrade target.
4. Verify readiness, portal login, and at least one integration read flow.

## Restore Validation

At minimum, a restore rehearsal should confirm:

- API and workers become ready
- operator login still works
- search can read restored data
- webhook, replay, and AI flows can resume from restored durable state
