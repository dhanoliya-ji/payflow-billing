# Problem statement

## Context

Any company selling software on a subscription has to answer three questions, every
day, without getting them wrong:

1. **Who owes us what, right now?**
2. **Did we actually collect it?**
3. **If not, what are we doing about it?**

A surprising amount of software gets this wrong in ways that are invisible until an
auditor, a customer, or a bank statement disagrees with the database. Billing is
unusual among CRUD problems because **the bugs are silent and the damage compounds**.
A web application that renders the wrong colour is noticed immediately; a billing
system that drops a cent per invoice, bills a customer twice for the same usage, or
lets a cancelled subscription keep charging is discovered weeks later, in aggregate,
by someone who now distrusts every number the system has ever produced.

## The problem

**Build a multi-tenant subscription billing service whose correctness properties are
enforced by its design rather than by the discipline of whoever touches it next.**

Concretely, the system must make the following classes of failure structurally
difficult:

### 1. Money that does not add up

Floating-point money, inconsistent rounding, and mixed currencies silently corrupt
totals. A per-unit price of \$0.002 rounded to two decimals becomes \$0.00, and every
metered customer is billed nothing.

*Required*: a single representation of money that carries its currency, rounds once at
a defined boundary, and refuses to mix currencies.

### 2. Time handled as arithmetic instead of as a calendar

A subscription starting on 31 January renews on 28 February, then 31 March. Adding
30-day blocks instead walks the anniversary backwards several days a year; adding a
month naively throws on a date that does not exist. Usage recorded at exactly midnight
on a period boundary is billed by both periods, or by neither.

*Required*: calendar-correct period arithmetic and a period interval whose endpoints
belong to exactly one period.

### 3. Cross-tenant data leakage

In a multi-tenant system every query needs a tenant predicate. If that predicate is a
convention, a single forgotten `WHERE` clause exposes one customer's financial records
to another, and no test that checks one tenant in isolation will ever catch it.

*Required*: tenant scoping enforced at the data-access layer by default, so the unsafe
query is the one that has to be written deliberately.

### 4. A state machine that only exists in the reader's head

Subscriptions move between trialing, active, past due, paused, cancelled and expired.
If those transitions are scattered across service methods, the illegal ones — resuming
a cancelled subscription, billing an expired one — are reachable and will eventually be
reached.

*Required*: one transition table, one enforcement point, and terminal states with no
outgoing edges.

### 5. Payment failures treated as exceptions rather than as the normal case

Most failed subscription revenue is not customers choosing to leave. It is expired
cards, temporary declines, and banks refusing without a reason. A system that treats a
declined charge as an error — retrying it immediately, or not at all — converts
recoverable revenue into permanent churn.

*Required*: declines are ordinary outcomes with reason codes, a retry schedule that is
policy rather than a constant, and a distinction between retriable and permanent
failures.

### 6. Operations that are not safe to repeat

Networks time out. Workers crash between charging a card and committing the result.
Metering agents retry. If any of "record this usage", "charge this invoice", or "run
the billing cycle" is not safe to repeat, the system double-bills under exactly the
conditions where it is least observable.

*Required*: idempotency on every externally triggered write and on every internal
operation a crash can interrupt.

### 7. State changes that nobody downstream hears about

Provisioning, emails and analytics all need to know when a subscription changed. The
two obvious implementations are both lossy: publishing inside the transaction announces
things that may roll back, and publishing after it loses the message on a crash.

*Required*: notification recorded atomically with the state change it describes.

### 8. Numbers nobody can check

A billing system's output is financial reporting. If revenue, churn and recovery are
computed by the same code that produced the data, a bug is invisible — the report
agrees with the database because both are wrong in the same way.

*Required*: analytics computed from the stored ledger, independently of the code that
wrote it, and verifiable from outside the application.

## Constraints

| Constraint | Reason |
| --- | --- |
| No real payment processor | PayFlow is a reference implementation. Handling real card data has regulatory obligations far beyond its scope. The gateway is a port with a realistic simulator behind it. |
| PostgreSQL as the only datastore | Billing needs transactions. Spreading an invoice and its payment across two stores means a partial failure leaves money unaccounted for. |
| Modular monolith, not microservices | The boundaries here are real, but the transactional coupling between "issue invoice", "charge card" and "update subscription" is also real. Splitting them across services trades a compiler-checked boundary for a distributed transaction. |
| No tax engine | Tax is jurisdiction-specific and genuinely hard. `Total` is kept distinct from `Subtotal` so adding one later does not change the meaning of any stored column. |

## Out of scope

Explicitly not attempted, and not pretended: real payment processing, tax calculation,
revenue recognition under ASC 606 / IFRS 15, multi-currency conversion, invoice PDF
rendering, dunning email delivery, and a customer-facing billing portal. The dashboard
is an operations view, not a product.

## What "done" looks like

The system is correct if, for a year of simulated billing:

- every invoice's total equals the sum of its lines, and every line equals its quantity
  times its unit price;
- every tenant's invoice numbering is contiguous, with no gaps and no numbers shared
  across tenants;
- no row references a parent row belonging to a different tenant;
- no usage record appears on two invoices;
- every subscription's status history is a walk through legal transitions only;
- and the revenue, churn and recovery figures can be recomputed from the ledger alone
  and agree with what the system reports.

The last four are checked by
[`analytics/payflow_analytics.ipynb`](../analytics/payflow_analytics.ipynb) against a
real database, from outside the application. See
[OBJECTIVES.md](OBJECTIVES.md) for how each of these is addressed.
