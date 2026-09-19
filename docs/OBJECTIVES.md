# Objectives

Each objective below states what is required, how PayFlow meets it, and how you can
check it for yourself. The numbering matches the failure classes in
[PROBLEM_STATEMENT.md](PROBLEM_STATEMENT.md).

---

## O1 — Money is exact, self-describing, and rounded once

**Requirement.** One representation of an amount that carries its currency, applies a
single rounding rule, and refuses to mix currencies.

**How.** [`Money`](../src/PayFlow.Domain/Common/Money.cs) is a readonly record struct of
`decimal` plus an ISO-4217 code, mapped by EF Core as a complex type — two real columns,
no extra table.

It stores **four** decimal places, not two. This is the non-obvious part: a metered
plan priced at \$0.002 per API call is a real thing to sell, and money rounded to cents
at construction stores that rate as \$0.00 and bills every call for nothing. Two
decimals is a property of what a customer can be *charged*, not of every amount in the
system, so rounding to minor units happens at exactly three boundaries — invoice line
amounts, invoice totals, and payment amounts — via an explicit `Round()`.

Rounding is banker's rounding. Away-from-zero rounding biases a long run of half-cent
amounts consistently upward, which across thousands of lines is a real, one-directional
error.

Arithmetic across currencies throws rather than coercing.

**Verify.** `MoneyTests` covers sub-cent unit prices, the rounding boundary, and
cross-currency rejection. `Metered_usage_is_invoiced_at_the_plan_rate_and_only_once`
asserts that 25,000 calls at \$0.002 with 10,000 included bills \$30.00.

---

## O2 — Time is calendar arithmetic, and periods are half-open

**Requirement.** Renewals that keep their anniversary, and period boundaries that belong
to exactly one period.

**How.** [`BillingInterval.Advance`](../src/PayFlow.Domain/Plans/BillingInterval.cs) uses
`AddMonths`/`AddYears`, which clamp to the end of a shorter month: a subscription
starting 31 January renews 28 February (29 in a leap year) and then 31 March. Adding
30-day blocks instead walks the anniversary earlier every year.

[`BillingPeriod`](../src/PayFlow.Domain/Subscriptions/BillingPeriod.cs) is the half-open
interval `[Start, End)`. A period ending at midnight and the next one starting at the
same instant do not both contain it, so usage recorded exactly on the boundary is billed
once. `ElapsedFraction` is computed from ticks, not whole days, so a plan change at
midday is not rounded to a free or a fully charged day.

**Verify.** `BillingPeriodTests` covers month-end clamping, leap years, half-open
containment, and tick-level proration fractions.

---

## O3 — Tenant isolation is the default, not a convention

**Requirement.** A forgotten tenant predicate must not be able to leak data.

**How.** [`PayFlowDbContext`](../src/PayFlow.Infrastructure/Persistence/PayFlowDbContext.cs)
applies a global query filter to every tenant-scoped entity. `db.Invoices.ToListAsync()`
returns *this tenant's* invoices; there is no spelling of that query that quietly
returns everyone's. `IgnoreQueryFilters()` is the deliberate, greppable escape hatch and
is used in exactly two places, both documented.

The tenant itself comes from an authenticated API key, not from a client-supplied
header — see O9.

Background jobs have no request to take a tenant from, so
[`TenantScopedWorker`](../src/PayFlow.Workers/TenantScopedWorker.cs) enumerates tenants
and opens a DI scope per tenant with that tenant pinned. The filters do their job inside
the workers too, rather than the workers needing a privileged unfiltered path.

**Verify.** `One_tenant_cannot_see_or_reach_another_tenant_s_data` and
`One_tenant_s_key_cannot_read_another_tenant_s_record`. Independently, the analytics
notebook queries PostgreSQL directly — underneath the filters — and asserts that no
invoice, payment, invoice line or usage record references a parent row in another
tenant.

---

## O4 — One state machine, one enforcement point

**Requirement.** Illegal subscription transitions must be unreachable.

**How.** Every transition in
[`Subscription`](../src/PayFlow.Domain/Subscriptions/Subscription.cs) funnels through a
private `Transition` method that consults a single `CanTransition` table. `Status` has a
private setter and nothing outside the class sets it. Terminal states have no outgoing
edges by construction.

Cancellation defaults to **end of period**, which is what customers mean: they keep what
they paid for. The billing cycle finalises it at the next renewal, and it can be
withdrawn until then.

**Verify.** `SubscriptionTests` walks the legal and illegal transitions, including a
test asserting that *no* transition leads out of a terminal state.

---

## O5 — Declines are a normal outcome with a policy

**Requirement.** Retry failed collections on a schedule, distinguishing recoverable from
permanent failures.

**How.** [`DunningPolicy`](../src/PayFlow.Domain/Payments/DunningPolicy.cs) is a value
object, not a constant: a tenant selling a \$9 consumer plan and one selling a \$9,000
enterprise plan should not chase a failure the same way. The default retries at 1, 3, 5
and 7 days — deliberately widening, because retrying a decline minutes later mostly
reproduces it and, on some card networks, counts against the merchant's retry allowance.

`ChargeResult.IsRetriable` separates the two kinds of failure. An expired or blocked
card fails identically on every attempt, so it is written off immediately rather than
consuming the schedule.

Failed attempts are stored as rows. A system that records only successes cannot explain
where revenue went.

**Verify.** `DunningTests` drives each branch with a scripted gateway: retry-then-
recover, schedule exhaustion leading to write-off and expiry, and permanent declines
short-circuiting the schedule. The notebook's recovery curve shows the real shape —
~89% on the first attempt, decaying to zero by the fourth.

---

## O6 — Every write is safe to repeat

**Requirement.** Retries and crashes must not double-bill.

**How.** Four separate mechanisms, because there are four distinct ways to double-bill:

| Risk | Mechanism |
| --- | --- |
| Metering agent retries a usage report | Client `idempotencyKey`, enforced by a unique index — not a read-then-write two concurrent retries can both pass |
| Worker crashes after charging, before committing | Charge idempotency key derived from invoice id + attempt number, so the replay returns the original capture |
| Billing cycle runs twice | An invoice already covering a period blocks a second one, backed by an index |
| Two runs allocate the same invoice number | `INSERT … ON CONFLICT … RETURNING` takes a row lock; `SELECT MAX(number)+1` hands both the same number |

**Verify.** `Retried_usage_reports_with_the_same_key_are_recorded_once`,
`Running_the_cycle_twice_does_not_bill_the_same_period_twice`,
`A_charge_carries_a_stable_idempotency_key_per_attempt`, and
`Invoice_numbers_are_sequential_and_gap_free_within_a_tenant`.

---

## O7 — State changes and their notifications commit together

**Requirement.** "It happened" and "someone was told" must not diverge.

**How.** Aggregates raise domain events; `SaveChangesAsync` drains them into an
[outbox table](../src/PayFlow.Infrastructure/Outbox/OutboxMessage.cs) inside the same
transaction. A rollback takes the message with it; a crash after commit leaves the
message for the dispatcher to deliver.

Delivery is at-least-once, so consumers must be idempotent — which is why every event
carries the id of the thing it happened to. PayFlow ships no broker, so the dispatcher
writes a structured log line; swapping that for a real publisher is a change to one
method, not to the billing path.

**Verify.** `Domain_events_reach_the_outbox_in_the_same_transaction`.

---

## O8 — The numbers are independently checkable

**Requirement.** Reporting that can disagree with the implementation, and therefore
catch it.

**How.** [`analytics/payflow_analytics.ipynb`](../analytics/payflow_analytics.ipynb)
reads PostgreSQL directly and recomputes MRR, revenue realisation, dunning recovery,
aging, and cohort retention from the stored ledger — not from the application's own
aggregates. Its last two cells are assertions, not illustrations: cross-tenant
references must be zero, and invoice sequences must be contiguous.

The data it reads is generated by [`PayFlow.Seeder`](../src/PayFlow.Seeder), which drives
the **real** `BillingCycleService` against a clock it controls. Its only inputs are
signups, usage, cancellations and plan changes; every invoice, retry and write-off is the
engine's own output. A billing bug therefore shows up as a wrong curve rather than being
hidden by fixture data.

**Verify.** Run the seeder, then the notebook. Both are executed in CI.

---

## O9 — Requests are authenticated, and the tenant is derived from the credential

**Requirement.** A caller must not be able to choose which tenant's data it sees.

**How.** The original implementation resolved the tenant from an `X-Tenant-Id` header
and fell back to a hard-coded tenant when it was missing or unparseable — so any caller
could read any tenant's billing data by sending a GUID, and a caller sending nothing
landed in the seed tenant.

The tenant is now derived from an API key the caller proves it holds, compared in fixed
time over a SHA-256 digest. Missing and wrong keys are rejected identically, because
distinguishing them tells an attacker whether a guessed key belongs to some other
tenant. An unauthenticated request resolves to no tenant at all.

The host refuses to start with anonymous development access enabled outside the
Development environment, and refuses to start with no tenants configured rather than
401-ing every request with no explanation.

**Verify.** `ApiTests` covers unauthenticated, wrongly authenticated, bearer-token and
cross-tenant access.

---

## O10 — Failures are legible in production

**Requirement.** When something goes wrong, the logs and metrics should say what.

**How.** Structured JSON logging, so the tenant and subscription ids on the billing path
survive as queryable fields. RFC 9457 problem responses that distinguish 404, 400
(malformed — never worth retrying) and 409 (well-formed but the state forbids it — may
succeed later), each carrying a `traceId`; internal exception messages are never echoed
to callers.

Metrics are **business** counters — invoices issued, collection attempts by outcome,
amount captured, write-offs — because HTTP 200s stay flat while every card is being
declined. They are exposed at `/metrics` in Prometheus format.

The billing cycle catches per-subscription failures and continues: one tenant's bad row
must not stall billing for everyone else. The count surfaces in the run report.

---

## Non-objectives

Real payment processing, tax, revenue recognition, currency conversion, invoice PDFs,
dunning emails, and a customer-facing portal. See
[PROBLEM_STATEMENT.md](PROBLEM_STATEMENT.md#out-of-scope).
