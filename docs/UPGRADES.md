# Upgrades

LawWatcher `1.x` supports sequential upgrades between tagged releases.

## Upgrade Contract

- use SemVer-tagged releases
- upgrade sequentially between tagged versions
- do not jump across undocumented release gaps
- use the release bundle for the target version
- the `1.0` freeze removes legacy pre-`1.0` runtime names from the public contract
- the `1.0` freeze requires bearer auth for integration reads and the canonical `integration:read` scope at runtime

## Schema Upgrades

- numbered SQL scripts remain the source of schema changes
- the SQL bootstrap now tracks them in `[lawwatcher].[schema_migrations]`
- existing pre-`1.0` databases are baselined into that ledger on first post-`1.0` bootstrap

## Recommended Upgrade Flow

1. Take SQL Server and MinIO backups.
2. Stop the running production bundle.
3. Replace the production env file image tags with the new SemVer release.
4. Start the new bundle.
5. Confirm schema migration completion.
6. Run the production smoke or equivalent release verification.

## Upgrade Validation

An upgrade is not complete until:

- `/health/ready` is green for API, portal, and workers
- operator login works
- the integration OpenAPI document is reachable
- replay/backfill and at least one AI/search path work on the upgraded runtime
