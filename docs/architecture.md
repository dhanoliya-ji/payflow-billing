# Architecture

PayFlow is a modular monolith: one deployable web process and an optional worker process, with strict project boundaries. Domain code has no infrastructure dependencies. Application contains use-case contracts. Infrastructure owns EF Core, PostgreSQL, and Redis adapters.

Every persisted aggregate carries a `TenantId`. `PayFlowDbContext` applies global query filters so accidental cross-tenant reads are blocked by default. Tenant resolution is deliberately an injectable boundary; production should populate `TenantContext` from a signed host/token claim and reject missing tenants.

Billing integrations should be added behind application ports (provider clients, idempotency store, and outbox). A future migration should add an outbox table and durable job scheduling before enabling payment side effects. Health checks distinguish liveness (`/health`) and readiness (`/health/ready`).
