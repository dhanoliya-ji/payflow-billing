# PayFlow

PayFlow is a production-oriented modular monolith foundation for multi-tenant subscription billing. It is intentionally small: the solution establishes boundaries, persistence, operational endpoints, and an extensible subscription state machine without pretending to implement a complete payment provider.

## Structure

- `src/PayFlow.Web` – ASP.NET Core host, health/OpenAPI, Razor/HTMX dashboard
- `src/PayFlow.Domain` – entities, value objects, and subscription transitions
- `src/PayFlow.Application` – use-case contracts and DTOs
- `src/PayFlow.Infrastructure` – EF Core/PostgreSQL, tenant context, Redis
- `src/PayFlow.Workers` – background worker host
- `tests/PayFlow.Domain.Tests` – domain tests

## Run locally

```bash
docker compose up -d postgres redis
dotnet restore
dotnet run --project src/PayFlow.Web
```

The web app exposes `/health`, `/health/ready`, `/openapi/v1.json`, and `/dashboard`.
Set `ConnectionStrings__Postgres` and `ConnectionStrings__Redis` for non-default environments.

## Architecture

See [docs/architecture.md](docs/architecture.md). Terraform in `deploy/terraform` is a starter for a managed PostgreSQL/Redis deployment and should be adapted to the target cloud.
