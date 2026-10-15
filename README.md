# release-guard

An order-processing API in C# on .NET 10, and the delivery machinery that proves each release healthy on EKS before the next one may follow.

Status: Phase 2 of 6. The application runs and is tested locally. The PR pipeline, release tooling and EKS delivery come next. Design decisions are in [docs/adr](docs/adr).

## The service

| Endpoint | Behavior |
|---|---|
| `POST /orders` | Requires `Idempotency-Key`. Same key and body returns the original order (`200`, `Idempotent-Replayed: true`). Same key with a different body returns `422`. Concurrent duplicates create exactly one order. |
| `GET /orders/{id}` | Returns the order with a strong `ETag` (its row version). |
| `POST /orders/{id}/transitions` | Requires `If-Match`. Illegal moves return `409`, a stale version returns `412`, a missing or weak tag returns `428`. |
| `GET /health/live` | Process only. A database outage does not restart pods. |
| `GET /health/ready` | Database reachable and schema at least as new as this build. |
| `GET /info` | Service name, release version and commit. |

Lifecycle: `Received → Confirmed → Shipped → Delivered`, cancellable until shipped.

Implementation notes:

- **Optimistic concurrency** uses PostgreSQL's `xmin` system column as the row version. The update is conditional on the version the client sent, so concurrent writers cannot overwrite each other.
- **Idempotency** is a primary key on the client's key plus a request fingerprint, inserted in the same transaction as the order. The database decides which concurrent request wins.
- **Least privilege in the database**: `orders_migrator` owns the schema and runs migrations from a separate process (`dotnet Orders.Api.dll migrate`). `orders_app` can only select, insert and update. In AWS both authenticate with RDS IAM tokens, so the application holds no database password.
- **Observability**: Serilog compact JSON logs with trace and span IDs. OpenTelemetry traces and metrics (ASP.NET Core, Npgsql, and an `Orders.Api` meter) exported over OTLP when a collector is configured.
- **Container**: chiseled .NET runtime image pinned by digest, numeric non-root user, no shell, runs with a read-only root filesystem.

## Rollback-safe migrations

Migrations are expand-only, so the previous release always runs against the current schema and an application rollback never needs a database rollback.

- `scripts/migration-safety.sh --from <migration>` rejects destructive DDL (drops, renames, type changes, new `NOT NULL` without a default). `scripts/tests/migration-safety.test.sh` covers each case.
- `scripts/n-minus-1.sh <previous-release>` migrates a fresh database with the current migrations, then runs the previous release's integration tests against it.

## Run it locally

Requires the .NET 10 SDK and Docker.

```sh
dotnet tool restore
dotnet test                                  # unit + Testcontainers integration tests

export ORDERS_MIGRATOR_PASSWORD=$(openssl rand -hex 16) ORDERS_APP_PASSWORD=$(openssl rand -hex 16)
docker compose -f deploy/local/docker-compose.yml up --build -d
curl -s localhost:18080/health/ready
curl -s -H 'Idempotency-Key: first-order' -H 'Content-Type: application/json' \
  -d '{"sku":"SKU-1","quantity":2}' localhost:18080/orders
docker compose -f deploy/local/docker-compose.yml down -v
```

Set `API_PORT` to change the host port.
