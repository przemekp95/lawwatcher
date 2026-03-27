# Absolute Done TODO

Ten plik opisuje, co jeszcze trzeba zamknac, zeby uczciwie powiedziec:

"LawWatcher jest zrobiony w calosci, bez istotnych brakow funkcjonalnych, architektonicznych i operacyjnych."

To nie jest backlog "mile to have". To jest backlog zamkniecia, zaktualizowany do obecnego stanu repo.

## Definition Of Done

Mozemy powiedziec, ze projekt jest "absolutely done" dopiero wtedy, gdy jednoczesnie sa spelnione wszystkie warunki ponizej:

- `dev-laptop` dziala end-to-end bez seed-only obchodow i bez recznego dogrzebywania sie do stanu.
- `sqlserver` jest glownym, normalnym runtime dla core flow, a nie tylko czesciowym adapterem.
- `RabbitMQ + MassTransit` sa realnie wpiete jako normalny transport messagingowy i maja swiezy dowod wykonaniowy na brokerze, nie tylko kod i smoke harnessy.
- `full-host` istnieje jako prawdziwy runtime z prawdziwymi workerami, nie tylko jako deklaracja topologii.
- search backendi sa domkniete: `ProjectionIndex`, `SqlFullText` i opcjonalny `OpenSearch/HybridVector` maja realne adaptery i swieze dowody wykonaniowe dla profili, ktore deklarujemy.
- object/document pipeline jest domkniety end-to-end na docelowym backendzie, a runtime capability truthfulness odpowiada rzeczywistemu zachowaniu.
- portal i API pokrywaja docelowy sposob obslugi produktu, a nie tylko dashboard, seed data i smoke-only write pathy.
- verification obejmuje build, runtime specs, browser-level verification, SQL runtime, messaging runtime i profile operacyjne, ktore deklarujemy jako wspierane.

## Current Honest Status

Na dzis repo jest wyraznie dalej niz "laptop-first MVP", ale nie jest jeszcze "absolutely done".

Mozna uczciwie powiedziec:

- modular monolith, bounded contexts, CQRS split, SQL event store/outbox/inbox i worker ownership sa juz realne
- operator auth, cookie session i browser-safe `CSRF/antiforgery` istnieja w API i sa uzywane przez portal `/admin`
- portal nie jest juz read-only; ma browser CRUD dla `operators`, `profiles`, `subscriptions`, `webhooks` i `api clients`
- lokalny LLM jest project-managed przez `ollama`, z bootstrapem i pinned modelem
- sporo read/write slice'ow ma juz SQL-backed runtime
- broker-ready `RabbitMQ + MassTransit` jest juz zaimplementowane dla glownych flow i ma swieze dowody wykonaniowe w wspieranych profilach Docker
- dawny windows-only lane `LocalDB + portable RabbitMQ + PowerShell broker smoke` zostal usuniety z repo, zeby nie utrzymywac niewspieranego kontraktu uruchomieniowego
- `MinIO` ma juz swiezy end-to-end green dla `act` AI grounding na realnym backendzie S3-compatible, bez uzycia filesystem fallback
- `docker` dev-laptop runtime jest juz realny dla `sqlserver + rabbitmq + minio + api + portal + worker-lite`, a profil `ai` ma swiezy green z dockerowym `ollama`
- `docker` full-host runtime jest juz realny i ma swiezy green dla `api + portal + worker-projection + worker-notifications + worker-replay + worker-documents + sqlserver + rabbitmq + minio + ollama`
- API, `worker-lite`, `worker-ai` i `worker-documents` maja juz health/readiness endpoints i swiezy health smoke
- browser verification, runtime smoke, signed webhook smoke, AI grounding smoke i SQL specs istnieja i byly uruchamiane

Nie mozna jeszcze uczciwie powiedziec:

- ze compose/images/deploy sa finalnie domkniete
- ze object/document pipeline ma juz finalny, jednoznacznie zamkniety backend i capability stance dla OCR/embeddings

## Closing Backlog

### 1. RabbitMQ + MassTransit Runtime Closure

Status juz zrobione:

- `LawWatcher.Api`, `Worker.Lite` i `Worker.Ai` maja broker-ready wiring
- SQL outbox jest relayowany do RabbitMQ dla gotowych slice'ow
- broker consumers i smoke runnery istnieja juz dla:
- `AiEnrichmentRequestedIntegrationEvent`
- `ReplayRequestedIntegrationEvent`
- `BackfillRequestedIntegrationEvent`
- `BillAlertCreatedIntegrationEvent`
- `BillImportedIntegrationEvent`
- `BillDocumentAttachedIntegrationEvent`
- `MonitoringProfileCreatedIntegrationEvent`
- `MonitoringProfileRuleAddedIntegrationEvent`
- `MonitoringProfileAlertPolicyChangedIntegrationEvent`
- `MonitoringProfileDeactivatedIntegrationEvent`
- `ProfileSubscriptionCreatedIntegrationEvent`
- `ProfileSubscriptionAlertPolicyChangedIntegrationEvent`
- `ProfileSubscriptionDeactivatedIntegrationEvent`
- `WebhookRegisteredIntegrationEvent`
- `WebhookUpdatedIntegrationEvent`
- `WebhookDeactivatedIntegrationEvent`
- `LegislativeProcessStartedIntegrationEvent`
- `LegislativeStageRecordedIntegrationEvent`
- `PublishedActRegisteredIntegrationEvent`
- `ActArtifactAttachedIntegrationEvent`
- `worker-lite` w broker mode nie uruchamia juz rownolegle normalnych scan/dispatch loops dla projection i notification flows; broker consumers sa glowna sciezka runtime dla obsluzonych kategorii

Pozostalo:

- [x] Miec swiezy end-to-end green dla co najmniej jednego broker path na prawdziwym `RabbitMQ`, a nie tylko harness gotowy do uruchomienia.
- [x] Miec swiezy end-to-end green dla krytycznych kategorii: `ai`, `replay/backfill`, `projection refresh`, `admin catch-up`.
  Status:
  wspierane profile Docker `dev-laptop`, `dev-laptop --include-ai`, `full-host` i `full-host --include-opensearch` maja juz swiezy green na prawdziwym `RabbitMQ` w kontenerach.
- [ ] Domknac retry, defer, poison handling i DLQ.
- [ ] Uporzadkowac stance runtime: SQL poller jako jawny fallback/recovery, a nie rownorzedna normalna sciezka.
- [ ] Odtworzyc wspierany, kontenerowy proof, ze write path nie czeka na AI/OCR/search/dispatch po stronie broker mode.
  Status:
  dawny proof byl oparty o usuniety windows-only lane `LocalDB + PowerShell`; po czyszczeniu kontraktu trzeba go odtworzyc jako Linux/Docker-first smoke.

Exit criteria:

- `RabbitMQ` jest realnie uzywane przez hosty na maszynie z dzialajacym brokerem
- jest swiezy end-to-end smoke przez broker dla deklarowanych glownych flow laptop-first, nie tylko dla jednego wybranego slice'a
- outbox row zmienia sie z `pending` do `published`, a consumer zapisuje inbox idempotency po odbiorze z brokera
- fallback SQL poller ma jawnie okreslona role operacyjna

### 2. Real Full-Host Topology

Pozostalo:

- [x] Utworzyc prawdziwe projekty hostow:
- `worker-documents`
- `worker-projection`
- `worker-notifications`
- `worker-replay`
- [x] Rozdzielic ownership z `worker-lite` na te hosty bez lamania kontraktow.
- [x] Podpiac je do compose i publish flow.
- [x] Udowodnic, ze `full-host` startuje i wykonuje swoje flow osobnymi workerami.
  Status:
  `ops/run-docker-full-host-smoke.sh` jest juz swiezo zielony dla `api + portal + worker-projection + worker-notifications + worker-replay + worker-documents + sqlserver + rabbitmq + minio + ollama`, z completed `ai`, `replay`, `backfill`, search hits i notification dispatch.

Exit criteria:

- wspierany `full-host` ma jasno okreslony, zaimplementowany zestaw hostow
- jest swiezy runtime smoke `full-host`

### 3. Search Finalization

Status juz zrobione:

- `ProjectionIndex` i truthful `SqlFullText` fallback semantics istnieja
- runtime truthfulness dla `ProjectionIndex` versus `SqlFullText` jest juz obslugiwane w API

Pozostalo:

- [x] Domknac realny `OpenSearch` adapter dla search/indexing.
- [x] Dodac realny `HybridVector` indexing/query path.
- [x] Zastapic placeholder/dev-only embedding path tam, gdzie obiecujemy semantic/hybrid behavior.
- [ ] Zweryfikowac runtime branch `SqlFullText` na hoscie, gdzie FTS jest faktycznie zainstalowany.
- [x] Udowodnic capability truthfulness dla trzech stanow:
- `ProjectionIndex`
- `SqlFullText`
- `HybridVector`

Exit criteria:

- `GET /v1/system/capabilities` i `GET /v1/search` poprawnie raportuja realny backend
- istnieje swiezy smoke dla `SqlFullText`
- istnieje swiezy smoke dla `OpenSearch/HybridVector`
  Status:
  `ops/run-docker-full-host-smoke.sh --include-opensearch` jest juz swiezo zielony i potwierdza backend `HybridVector (1)` na `/v1/search` oraz `/v1/system/capabilities`, zdrowy `OpenSearch` i realne `nomic-embed-text` embeddings.

### 4. Document And Artifact Pipeline

Status juz zrobione:

- object/document pipeline istnieje
- AI grounding przez stored artifacts istnieje
- `MinIO` jest realnym backendem za `IDocumentStore`
- `ollama` jest project-managed, a local LLM ma bootstrap i smoke path

Pozostalo:

- [x] Miec swiezy end-to-end runtime smoke na realnym backendzie `MinIO`, nie tylko local filesystem fallback.
- [ ] Zdecydowac, czy `PlainTextOcrService` i `DeterministicEmbeddingService` sa finalne czy tylko laptop-first adaptery.
- [ ] Jesli nie sa finalne, podmienic je na docelowe adaptery albo jawnie zdegradowac capability w produkcie i dokumentacji.
- [ ] Domknac retention/cleanup dla artefaktow dokumentowych i wynikow AI.

Exit criteria:

- dokument z `LegalCorpus` trafia do realnego object store, jest odczytywany przez AI flow i potwierdzone jest to swiezym smoke
- opis capability w README i runtime odpowiada faktycznemu zachowaniu

### 5. HTTP/API And Portal Closure

Status juz zrobione:

- backend admin CRUD istnieje dla `operators`, `profiles`, `subscriptions`, `webhooks` i `api clients`
- operator auth istnieje
- browser-safe `cookie + antiforgery` istnieje
- portal `/admin` robi browser CRUD dla tego admin surface
- M2M endpoints pozostaja bearer-only

Pozostalo:

- [ ] Zdecydowac, czy jest jeszcze jakis istotny operacyjny write surface poza obecnym admin CRUD i ingest-driven core flow.
- [ ] Jesli tak, dopiac brakujace write pathy swiadomie, a nie przez seed/bootstrap.
- [ ] Uporzadkowac role seed data tak, zeby byly tylko development/demo aid, a nie cicha proteza brakujacego flow.
- [x] Utrzymac i dokumentowac rozdzial browser auth versus M2M auth jako finalny kontrakt produktu.
  Status:
  [README.md](README.md) jawnie opisuje juz finalny kontrakt: browser admin flow idzie przez operator `cookie + antiforgery`, a `ai/replays/backfills` i pozostale M2M write pathy pozostaja bearer-only bez cookies i bez browser session. Swiezy proof istnieje zarowno po stronie backendu przez `ops/run-operator-admin-smoke.ps1`, jak i po stronie przegladarki przez `ops/run-operator-admin-browser-smoke.ps1`.

Exit criteria:

- seed data nie jest jedynym sposobem wprowadzania istotnych danych operacyjnych
- write surface jest zgodny z docelowym sposobem pracy produktu
- portal i API razem pokrywaja finalny operational surface, ktory deklarujemy jako wspierany

### 6. Packaging And Deployment

Pozostalo:

- [ ] Zamienic compose/image scaffolding na realne build/publish artifacts dla wszystkich hostow.
  Status:
  Bazowy `ops/compose/docker-compose.yml` i `ops/compose/docker-compose.full-host.yml` sa juz image-first i uzywaja jawnych `LAWWATCHER_*_IMAGE` zamiast lokalnego `build:`. Lokalne buildy zostaly odsuniete do `ops/compose/docker-compose.build.yml` oraz `ops/compose/docker-compose.full-host.build.yml`, `.github/workflows/publish-images.yml` przygotowuje linuxowe obrazy `ghcr.io/<owner>/lawwatcher-*`, a repo ma juz publiczny remote `https://github.com/przemekp95/lawwatcher`. Nadal brakuje swiezego dowodu, ze workflow rzeczywiscie wypchnal te obrazy do GHCR.
- [ ] Dodac finalne Dockerfile lub rownowazny packaging flow.
  Status:
  `ops/compose/Dockerfile.host` jest juz wspolnym packaging flow dla hostow, `.dockerignore` ogranicza kontekst, a workflow `publish-images.yml` buduje multi-arch `linux/amd64,linux/arm64` obrazy dla `api`, `portal`, `worker-lite`, `worker-ai`, `worker-documents`, `worker-projection`, `worker-notifications` i `worker-replay`. Nadal otwarty pozostaje runtime proof z samego GHCR, a nie tylko lokalnego build override.
- [ ] Zweryfikowac `docker compose up` dla deklarowanych profili.
  Status:
  `dev-laptop`, `full-host` i `full-host + opensearch` sa juz swiezo zweryfikowane lokalnie przez build override. Nadal otwarty jest swiezy proof image-first `docker compose pull && up` z realnymi obrazami z GHCR.
- [ ] Uporzadkowac env/config contracts tak, zeby byly zgodne z kodem i README.
  Status:
  `dev-laptop` i `full-host` env keys dla SQL sa juz wyrownane do `ConnectionStrings__LawWatcherSqlServer`, a env files maja juz jawne `LAWWATCHER_*_IMAGE`. Nadal trzeba dowiezc finalny proof, ze README i GHCR naming zgadzaja sie z rzeczywistym workflow push.

Exit criteria:

- deklarowane obrazy `ghcr.io/<owner>/lawwatcher-*` sa realnie publikowane i uruchamiane
- `dev-laptop` compose nie jest juz opisane jako scaffolding
- `full-host` ma realne hosty i swiezy green, a ewentualne brakujace hosty sa albo dowiezione, albo usuniete z deklarowanej topologii

### 7. Ops Hardening

Status juz zrobione:

- API, `worker-lite`, `worker-ai` i `worker-documents` maja health/readiness endpoints
- istnieje swiezy host health smoke dla `API + Worker.Lite + Worker.Ai`

Pozostalo:

- [x] Dodac monitoring podstawowych kolejek i stanu outbox/inbox.
  Status:
  `GET /v1/system/messaging` istnieje juz jako admin/system diagnostics endpoint i daje swiezy runtime snapshot `outbox/inbox`, grouped per `message_type` oraz `consumer_name`, z truthful `deliveryMode/pollerMode`. Swiezy green jest juz dowiedziony w wspieranych profilach Docker.
- [x] Dodac cleanup/retention dla `published outbox`, `processed inbox` i `event_feed`.
  Status:
  `POST /v1/system/maintenance/retention` istnieje juz jako admin-only endpoint dla SQL runtime i ma swiezy green przez `ops/run-retention-maintenance-smoke.ps1`, z realnym usunieciem starych rowow i zachowaniem nowych.
- [x] Podjac i dowiezc bezpieczna semantyke cleanup/retention dla `search_documents`.
  Status:
  `search_documents` maja juz jawnie opt-in cleanup przez `searchDocumentsRetentionHours` na `POST /v1/system/maintenance/retention`, oparty o `indexed_at_utc` zamiast ukrytego TTL. `ops/run-retention-maintenance-smoke.ps1` jest juz swiezo zielony i potwierdza usuniecie starych rowow oraz zachowanie nowych.
- [ ] Odtworzyc Linux/Docker-first structured-log proof dla kluczowych flow po usunieciu windows-only broker smoke lane.
  Status:
  structured log templates w kodzie nadal istnieja, ale dawny asercyjny proof opieral sie o usuniete PowerShell broker smoke'i i musi zostac odtworzony w wspieranym kontrakcie kontenerowym.
- [x] Dodac runbook dla awarii runtime i odtwarzania state/projection.
  Status:
  [docs/RUNBOOK.md](docs/RUNBOOK.md) istnieje juz jako operacyjny runbook dla `dev-laptop`, `ai`, `full-host` i `opensearch`, obejmuje first-response checks, health, Docker/RabbitMQ recovery, `/v1/system/messaging`, retention, broker smoke map, browser admin recovery i zasady bezpiecznego odtwarzania state/projection bez recznego kasowania `outbox/inbox`.

Exit criteria:

- operator ma jak rozpoznac:
- co nie dziala
- co utknelo
- co mozna bezpiecznie replayowac

### 8. Verification Closure

Status juz zrobione:

- `ApplicationSpecs` i `ArchitectureSpecs` istnieja i byly wielokrotnie uruchamiane
- browser verification dla portalu istnieje
- signed webhook smoke istnieje
- AI grounding smoke istnieje
- MinIO-backed AI grounding smoke istnieje i ma swiezy green
- operator admin browser smoke istnieje
- broker smoke runnery istnieja dla glownych broker-ready slice'ow

Pozostalo:

- [x] Miec swiezy broker-backed smoke, ktory faktycznie przechodzi na maszynie z dzialajacym `RabbitMQ`.
  Status:
  wspierane profile Docker `dev-laptop`, `dev-laptop --include-ai`, `full-host` i `full-host --include-opensearch` sa juz swiezo zielone na prawdziwym `RabbitMQ`.
- [ ] Miec swiezy runtime smoke:
- `dev-laptop/files`
- [x] Miec swiezy runtime smoke:
- `dev-laptop/sqlserver`
- `ai`
  Status:
  `ops/run-docker-dev-laptop-smoke.sh` i `ops/run-docker-dev-laptop-smoke.sh --include-ai` sa juz swiezo zielone na realnym Docker runtime z `sqlserver + rabbitmq + minio` oraz dockerowym `ollama`.
- [x] Miec swiezy runtime smoke:
- `full-host`
  Status:
  `ops/run-docker-full-host-smoke.sh` jest juz swiezo zielony na realnym Docker runtime z `api + portal + worker-projection + worker-notifications + worker-replay + worker-documents + sqlserver + rabbitmq + minio + ollama`.
- [x] Miec swiezy AI grounding smoke na docelowym object store backendzie, jesli finalnie to `MinIO` ma byc deklarowanym defaultem dla tego flow.
  Status:
  `ops/run-act-ai-grounding-minio-smoke.ps1` jest juz swiezo zielony na realnym `MinIO + Ollama`, ale nadal warto odtworzyc rownowazny shell/container proof, jesli ten smoke ma zostac elementem finalnego Linux-first kontraktu.
- [ ] Miec swiezy search smoke dla hosta z prawdziwym `SqlFullText`.
- [x] Miec swiezy smoke dla `OpenSearch/HybridVector`, jesli ten profil ma byc deklarowany jako wspierany.
  Status:
  `ops/run-docker-full-host-smoke.sh --include-opensearch` jest juz swiezo zielony na realnym Docker runtime z `api + portal + worker-projection + worker-notifications + worker-replay + worker-documents + sqlserver + rabbitmq + minio + ollama + opensearch`.

Exit criteria:

- wszystkie profile, ktore deklarujemy w README, maja swiezy dowod wykonaniowy

## Probably Optional, But Must Be Decided Explicitly

To nie sa automatycznie braki. To sa rzeczy, ktore trzeba jawnie zatwierdzic jako:

- finalne
- odlozone poza v1
- swiadomie niewspierane

Lista:

- [ ] Czy `ProjectionIndex` fallback zostaje finalnym laptop-first search backendem, czy ma byc tylko awaryjny?
- [ ] Czy `OpenSearch` jest wymagane do uznania `full-host` za done?
- [ ] Czy OCR ma byc "real OCR", czy wystarczy obecny capability level?
- [ ] Czy semantic search ma byc produkcyjnie wlaczone, czy tylko architektonicznie przygotowane?
- [ ] Czy finalnym object store defaultem ma byc zawsze `MinIO`, czy dopuszczamy jawnie rownorzedny finalny backend lokalny?

## Minimal Honest Statement Today

Dzis mozna uczciwie powiedziec:

"LawWatcher ma mocny laptop-first foundation, duza czesc runtime SQL-backed, operator-secured admin CRUD w API i portalu, project-managed lokalny LLM oraz swiezo zielone Docker i RabbitMQ runtime dla `dev-laptop`, `full-host`, `full-host + opensearch` i glownych brokerowych slice'ow. Nie jest jeszcze finalnie domkniety jako caly produkt. Najwieksze otwarte tematy to finalne packaging/deploy dla wszystkich deklarowanych hostow, swiezy runtime dowod dla prawdziwego `SqlFullText`, oraz koncowe domkniecie object/document runtime i pozostalego ops hardeningu."
