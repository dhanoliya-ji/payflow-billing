# Architecture

## Shape

PayFlow is a **modular monolith**: two deployable processes (a web host and a worker
host) over one database, with project references enforcing the layering.

```text
                    ┌──────────────┐          ┌───────────────┐
   HTTP ──────────► │ PayFlow.Web  │          │PayFlow.Workers│ ◄── timer
   (API key)        │              │          │               │
                    │ auth, routes │          │ billing cycle │
                    │ dashboard    │          │ dunning sweep │
                    │ /metrics     │          │ outbox drain  │
                    └──────┬───────┘          └───────┬───────┘
                           │                          │
                           └───────────┬──────────────┘
                                       ▼
                        ┌──────────────────────────────┐
                        │     PayFlow.Application      │
                        │  use cases + ports           │
                        │  BillingCycleService         │
                        │  IClock IPaymentGateway …    │
                        └───────┬──────────────┬───────┘
                                │              │
                 ┌──────────────▼───┐   ┌──────▼───────────────────┐
                 │ PayFlow.Domain   │   │ PayFlow.Infrastructure   │
                 │ entities, rules, │◄──│ EF Core, Npgsql, Redis,  │
                 │ state machine,   │   │ gateway simulator,       │
                 │ Money, proration │   │ outbox, idempotency      │
                 └──────────────────┘   └──────────┬───────────────┘
                                                   ▼
                                    PostgreSQL 17   ·   Redis 7 (optional)
```

Dependencies point inward. `PayFlow.Domain` references nothing. `PayFlow.Infrastructure`
implements ports declared in `PayFlow.Application`. Neither host references the other.

### Why a monolith

The boundaries here are real — a plan catalogue and a dunning engine are genuinely
different concerns — but the *transactional* coupling is also real. Issuing an invoice,
charging a card and updating a subscription either all happen or none do. Splitting
those across services replaces a compiler-checked boundary with a distributed
transaction, and distributed transactions over money are where billing systems go to
lose rows.

The project boundaries are the seam. If a module ever needs to be extracted, its
dependencies are already explicit.

---

## The one deliberate compromise

`PayFlow.Application` references `Microsoft.EntityFrameworkCore` in order to declare
[`IPayFlowDbContext`](../src/PayFlow.Application/Abstractions/IPayFlowDbContext.cs) — a
`DbSet`/`IQueryable` surface, not a provider dependency.

Strict Clean Architecture would put repositories here instead. That was rejected on
inspection of what the repositories would have to be. Billing reads are query-shaped —
"open invoices past their due date, with their lines, for this tenant, page 3" — and a
repository over them either returns `IQueryable` (the same coupling, with extra
indirection) or multiplies into dozens of single-use methods that change every time a
screen does.

What the abstraction does buy: use-case orchestration lives in the application layer
rather than leaking into infrastructure, and the layer is testable against any EF
provider.

What it costs: the application layer knows EF Core exists. Swapping ORMs would touch it.
That is a trade made with open eyes, not an oversight.

---

## Persistence

### Tenancy

Every tenant-scoped entity carries a `TenantId` and has a **global query filter**.
`db.Invoices.ToListAsync()` returns this tenant's invoices; there is no spelling of that
query that quietly returns everyone's. `IgnoreQueryFilters()` is the escape hatch and
appears in exactly two places:

- `DatabaseTenantDirectory`, which enumerates tenants — it has to cross tenants, because
  enumerating them is its job.
- The outbox dispatcher, whose table is infrastructure rather than tenant data.

Both are commented as such, and both are greppable.

The tenant comes from an authenticated API key. A client-supplied header would let any
caller choose whose data to read.

### Schema conventions

| Convention | Reason |
| --- | --- |
| snake_case tables and columns | Unquoted identifiers fold to lower case in PostgreSQL. A PascalCase schema forces every hand-written query — including the analytics notebook — to quote every identifier. |
| Enums stored as text | An ordinal column silently reinterprets every existing row the day someone inserts a new enum member in the middle. |
| `numeric(18,4)` for money | Four decimals so a sub-cent unit price survives storage. See [O1](OBJECTIVES.md#o1--money-is-exact-self-describing-and-rounded-once). |
| Real migrations, not `EnsureCreated` | `EnsureCreated` creates a schema but records no migration history, so the first real migration then fails against a database it did not create. |

One index is created by hand in the initial migration: `ix_subscriptions_period_end`.
EF Core cannot express an index over a member of a complex type
(`Subscription.CurrentPeriod.End`), and that column is the selective half of the billing
cycle's central query. It is consequently absent from the model snapshot; the migration
is the source of truth for it.

### Transactions

`IPayFlowDbContext.InTransactionAsync` wraps a unit of work. It joins an ambient
transaction rather than nesting, and uses the provider's execution strategy so a
transient failure retries the whole block rather than half of it.

---

## Billing model

**Billing is in arrears.** An invoice is issued when a period *closes*, for the period
that just ended. This is what lets a metered plan invoice usage that is only known once
the period is over, and it means "what did this customer consume" and "what were they
charged" describe the same window.

A billing cycle run, per tenant:

1. Find subscriptions whose period has ended and that are not terminal or paused.
2. Capture the closing period, **then** renew — the order matters, because renewing
   moves the window.
3. Build the invoice for the closed period: the recurring charge, metered usage grouped
   per metric (so the included allowance applies once per period, not once per record),
   and any pending proration adjustments.
4. Finalise it — assign the per-tenant invoice number and the due date.
5. Collect it through the gateway.

Trials produce no invoice at all, rather than a zero-total one nobody needs to read.

Mid-cycle plan and seat changes do not charge immediately. They produce a
`SubscriptionAdjustment` — a credit for the unused part of the old terms, a charge for
the rest of the period on the new ones — which the next invoice sweeps up. Charging a
customer \$7.43 the moment they add a seat is hostile and expensive in transaction fees.

---

## Payments and dunning

`IPaymentGateway` is a port. PayFlow never sees a card number: the customer holds an
opaque token the gateway issued, and collection is "charge this token this amount". That
keeps the service out of PCI scope and makes the whole payment path testable.

The only implementation shipped is
[`SimulatedPaymentGateway`](../src/PayFlow.Infrastructure/Payments/SimulatedPaymentGateway.cs),
built to behave like a processor where it matters rather than to always succeed:
outcomes are a deterministic function of the idempotency key (so a replay returns the
original capture), declines carry realistic reason codes split into retriable and
permanent, and retries succeed at a lower rate than first attempts.

Dunning is driven by `DunningPolicy` — retry offsets, payment terms, and whether
exhaustion expires the subscription — held as configuration rather than as constants.

---

## Observability

- **Structured JSON logs.** The billing path logs tenant, subscription and invoice ids;
  those only stay queryable if they survive as fields.
- **Business metrics** at `/metrics`, in Prometheus text format, built on
  `System.Diagnostics.Metrics` so the same instruments can feed an OpenTelemetry
  exporter later without touching the billing path. The exposition endpoint is
  hand-written because the OpenTelemetry Prometheus exporter is still pre-release; it
  swaps out cleanly once that ships.
- **Health**: `/health` is liveness, `/health/ready` also checks the database. The
  container healthcheck uses readiness, since that is what determines whether an
  instance can serve a request.

---

## Failure handling

| Failure | Behaviour |
| --- | --- |
| Redis unreachable | Falls back to an in-process cache at startup, and cache reads degrade to a miss at runtime. A billing API must not 500 because a cache node is rebooting. |
| One subscription in a bad state | The cycle logs it, continues with the rest, and surfaces the count in the run report. One tenant's bad row must not stall billing for everyone. |
| Gateway declines | An expected outcome, not an error. Recorded as a `Payment` row with its reason code. |
| Worker throws | The loop logs and survives to the next tick. A worker that dies silently stops billing until someone notices. |
| Outbox message fails repeatedly | Capped at 10 attempts. Retrying a poison message forever starves everything behind it. |

---

## Deployment

Two images, both non-root, built from
[`deploy/docker`](../deploy/docker). The web host owns the schema and applies
migrations at startup; the workers only read a schema someone else applied, because two
processes migrating at once race on the migration history table.

`deploy/terraform` is a starter for managed PostgreSQL and Redis and needs adapting to
a real target — it has no VPC, no subnet group, and no secret management.

## Known gaps

Stated rather than implied:

- The gateway is a simulator. There is no real processor integration.
- No tax engine. `Total` is kept distinct from `Subtotal` so adding one later does not
  change the meaning of any stored column.
- API keys are compared against configuration. In production only a hash should be at
  rest, in a secret store.
- The outbox dispatcher logs rather than publishing to a broker.
- No rate limiting on the API.
- Multi-currency is supported per customer, but there is no conversion: a tenant's MRR
  across mixed currencies is not a single number, and the analytics assume one.
