# Support

## Supported Product

LawWatcher `1.0` is a private self-hosted product. The supported install target is the `production` bundle only.

## Not Supported

- `dev` used as production
- production with disabled `Ollama`
- production with disabled `OpenSearch`
- placeholder secrets
- `latest` image tags treated as a release
- demo seed flags used as the operational bootstrap mechanism

## Compatibility Matrix

| Component | Supported line |
| --- | --- |
| LawWatcher images | SemVer-tagged GHCR images |
| Docker Compose | v2, as exercised by the repo workflows and smoke scripts |
| SQL Server | SQL Server 2022 line |
| RabbitMQ | 3.13 management line |
| MinIO | compose-pinned release from `ops/compose/docker-compose.yml` |
| Ollama | pinned local runtime used by the `production` bundle |
| OpenSearch | 2.18.0 |

## Product Surface

- operator/browser surface: cookie + CSRF
- integration surface: bearer-authenticated reads and writes, plus signed webhooks
- machine-readable integration contract: `/openapi/integration-v1.json`

## Operational References

- install and supported startup: [INSTALL.md](./INSTALL.md)
- configuration and bootstrap: [CONFIGURATION.md](./CONFIGURATION.md)
- backup and restore: [BACKUP-RESTORE.md](./BACKUP-RESTORE.md)
- upgrade path: [UPGRADES.md](./UPGRADES.md)
- incident handling: [RUNBOOK.md](./RUNBOOK.md)
