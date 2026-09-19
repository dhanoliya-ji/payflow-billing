# API

Base path `/v1`. The machine-readable contract is at `/openapi/v1.json`.

## Authentication

Every `/v1` endpoint and the dashboard require a tenant API key, in either header:

```http
X-Api-Key: pf_dev_acme_key
Authorization: Bearer pf_dev_acme_key
```

The key determines the tenant. There is no way to ask for another tenant's data —
requests carry no tenant identifier, and a record belonging to a different tenant
returns **404**, not 403, because telling a caller that an id exists but is not theirs
leaks the existence of other tenants' records.

Missing and incorrect keys are both rejected with **401** and an identical body.

`/health`, `/health/ready`, `/metrics` and `/openapi/v1.json` need no key.

In the `Development` environment, `Authentication:AllowAnonymousDevelopmentTenant`
allows unauthenticated requests as the development tenant. The host refuses to start
with it enabled anywhere else.

## Errors

Failures are [RFC 9457](https://www.rfc-editor.org/rfc/rfc9457) problem documents:

```json
{
  "type": "https://payflow.dev/problems/conflict",
  "title": "Operation not allowed in the current state",
  "status": 409,
  "detail": "Subscription is already Canceled.",
  "instance": "/v1/subscriptions/3f2a.../plan",
  "traceId": "0HN7K8M2V1L9P:00000003"
}
```

| Status | Meaning | Retry? |
| --- | --- | --- |
| 400 | The request is malformed or a value is invalid | No — it will fail the same way |
| 401 | No key, or the key is not recognised | No |
| 404 | No such record in this tenant | No |
| 409 | The request is well-formed, but the current state forbids it | Possibly — after the state changes |
| 500 | A bug. The message is logged, not returned | Yes, with backoff |

The 400/409 split is the useful one: a client can retry a 409 after the blocking
condition clears, and should never retry a 400.

## Resources

### Customers

| Method | Path | Purpose |
| --- | --- | --- |
| `POST` | `/v1/customers` | Create |
| `GET` | `/v1/customers` | List — `page`, `pageSize`, `search`, `includeArchived` |
| `GET` | `/v1/customers/{id}` | Fetch |
| `PATCH` | `/v1/customers/{id}` | Change email or display name |
| `PUT` | `/v1/customers/{id}/payment-method` | Store the gateway token used to charge |
| `DELETE` | `/v1/customers/{id}` | Archive — invoices and payments are retained |

Customers are archived, never deleted: invoices and payments must stay referentially
intact for reporting and audit.

`POST /v1/customers`:

```json
{
  "email": "alex@example.com",
  "displayName": "Alex Johnson",
  "currency": "USD",
  "externalReference": "crm-4412",
  "paymentMethodToken": "pm_card_visa"
}
```

The payment method token is an opaque handle the gateway issued. PayFlow never receives
card details, which keeps it out of PCI scope.

### Plans

| Method | Path | Purpose |
| --- | --- | --- |
| `POST` | `/v1/plans` | Create, optionally with metered rates |
| `GET` | `/v1/plans` | List — `includeArchived` |
| `GET` | `/v1/plans/{id}` | Fetch |
| `DELETE` | `/v1/plans/{id}` | Archive — existing subscriptions keep billing against it |

`interval` is `Weekly`, `Monthly`, `Quarterly` or `Yearly`. `pricingModel` is
`FlatRate`, `PerSeat` or `Metered`.

A metered plan:

```json
{
  "code": "METERED",
  "name": "Pay as you go",
  "amount": 19.00,
  "interval": "Monthly",
  "pricingModel": "Metered",
  "meteredRates": [
    { "metric": "api_calls", "unitPrice": 0.002, "includedQuantity": 10000 },
    { "metric": "storage_gb", "unitPrice": 0.15, "includedQuantity": 50 }
  ]
}
```

A plan's price and cadence never change in place — subscriptions already invoiced
against it would retroactively disagree with their own invoices. To reprice, archive the
plan, publish a new one, and move subscriptions with `POST /v1/subscriptions/{id}/plan`,
which prorates the switch.

### Subscriptions

| Method | Path | Purpose |
| --- | --- | --- |
| `POST` | `/v1/subscriptions` | Create |
| `GET` | `/v1/subscriptions` | List — `status`, `customerId`, `page`, `pageSize` |
| `GET` | `/v1/subscriptions/{id}` | Fetch |
| `POST` | `/v1/subscriptions/{id}/plan` | Change plan, prorated |
| `POST` | `/v1/subscriptions/{id}/quantity` | Change seat count, prorated |
| `POST` | `/v1/subscriptions/{id}/pause` | Suspend — does not bill or accrue dunning |
| `POST` | `/v1/subscriptions/{id}/resume` | Restart, or withdraw a pending cancellation |
| `DELETE` | `/v1/subscriptions/{id}` | Cancel — `?immediately=true` to end now |
| `GET` | `/v1/subscriptions/{id}/usage` | Current-period usage and what it would cost |

Cancellation defaults to the **end of the paid period**: the subscription stays
entitled, `cancelAtPeriodEnd` is set, and the billing cycle finalises it at renewal.
`POST .../resume` withdraws it until then.

Plan and quantity changes return what the change is worth:

```json
{
  "subscription": { "...": "..." },
  "credit": 50.00,
  "charge": 150.00,
  "net": 100.00
}
```

Nothing is charged at that moment. The credit and charge are carried to the next
invoice.

Lifecycle: `Trialing → Active → PastDue → Active`, with `Paused` reachable from the
live states and `Canceled`/`Expired` terminal. Illegal transitions return 409.

### Usage

| Method | Path | Purpose |
| --- | --- | --- |
| `POST` | `/v1/usage` | Record metered usage |
| `GET` | `/v1/usage` | List — `subscriptionId`, `metric`, `invoiced` |

```json
{
  "subscriptionId": "3f2a...",
  "metric": "api_calls",
  "quantity": 2500,
  "recordedAt": "2026-09-19T12:00:00Z",
  "idempotencyKey": "agent-batch-9174"
}
```

**Send an `idempotencyKey`.** Metering agents retry on timeouts, and a replayed report
without one becomes a second charge. The key is unique per tenant and a replay returns
the original record.

`recordedAt` is when the usage *happened*, not when it was reported, so a late report
still lands on the right invoice provided that invoice has not been issued yet.

### Invoices

| Method | Path | Purpose |
| --- | --- | --- |
| `POST` | `/v1/invoices` | Bill a subscription's current period on demand |
| `GET` | `/v1/invoices` | List — `status`, `customerId` |
| `GET` | `/v1/invoices/{id}` | Fetch with lines |
| `POST` | `/v1/invoices/{id}/void` | Cancel an unpaid invoice |
| `POST` | `/v1/invoices/{id}/collect` | Attempt collection through the gateway |

Statuses: `Draft`, `Open`, `Paid`, `PastDue`, `Uncollectible`, `Void`.

Voiding releases the usage records and adjustments the invoice consumed, so a corrected
invoice can pick them up. A paid invoice cannot be voided — refund the payment instead.

`POST .../collect` returns **200 with the outcome in the body** whether or not the card
was approved. A decline is a fully processed result, not a request error:

```json
{
  "payment": { "status": "Failed", "failureCode": "insufficient_funds", "attemptNumber": 1 },
  "succeeded": false,
  "invoiceStatus": "PastDue",
  "nextAttemptAt": "2026-09-20T12:00:00Z",
  "dunningExhausted": false
}
```

### Payments

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/v1/payments` | List attempts — `invoiceId`, `status` |
| `POST` | `/v1/payments/{id}/refund` | Refund in part or in full |

Failed attempts appear here too. A system that records only successes cannot explain
where revenue went. A refund reopens the invoice for the refunded amount.

### Billing operations

| Method | Path | Purpose |
| --- | --- | --- |
| `POST` | `/v1/billing/run-cycle` | Run one billing cycle now |
| `POST` | `/v1/billing/run-dunning` | Retry every invoice whose schedule has come due |

The workers do both on a schedule; these exist for manual and test-driven runs. Both are
safe to call repeatedly.

### Analytics

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/v1/analytics/mrr` | MRR and ARR, split by plan |
| `GET` | `/v1/analytics/revenue` | Collected, invoiced and written off by month — `months` |
| `GET` | `/v1/analytics/dunning` | Recovery rate by attempt number |
| `GET` | `/v1/analytics/aging` | Outstanding balance by days past due |
| `GET` | `/v1/analytics/declines` | Decline reasons and their share |

MRR counts only billable subscriptions and normalises every cadence to a monthly
figure — a \$990/year plan contributes \$82.50. Trials contribute nothing until they
convert.

## Paging

List endpoints take `page` (from 1) and `pageSize` (default 50, max 200) and return:

```json
{ "items": [], "totalCount": 0, "page": 1, "pageSize": 50, "totalPages": 0, "hasNextPage": false }
```

## Example: a subscription from signup to payment

```bash
KEY="pf_dev_acme_key"
API="http://localhost:8080/v1"

CUSTOMER=$(curl -s -X POST $API/customers -H "X-Api-Key: $KEY" \
  -H 'Content-Type: application/json' \
  -d '{"email":"alex@example.com","displayName":"Alex Johnson","paymentMethodToken":"pm_card_visa"}' \
  | jq -r .id)

PLAN=$(curl -s -X POST $API/plans -H "X-Api-Key: $KEY" \
  -H 'Content-Type: application/json' \
  -d '{"code":"STARTER","name":"Starter","amount":29,"interval":"Monthly"}' \
  | jq -r .id)

SUB=$(curl -s -X POST $API/subscriptions -H "X-Api-Key: $KEY" \
  -H 'Content-Type: application/json' \
  -d "{\"customerId\":\"$CUSTOMER\",\"planId\":\"$PLAN\"}" \
  | jq -r .id)

# Bill the current period now instead of waiting for it to close.
INVOICE=$(curl -s -X POST $API/invoices -H "X-Api-Key: $KEY" \
  -H 'Content-Type: application/json' \
  -d "{\"subscriptionId\":\"$SUB\"}" | jq -r .id)

curl -s -X POST $API/invoices/$INVOICE/collect -H "X-Api-Key: $KEY" | jq
curl -s $API/analytics/mrr -H "X-Api-Key: $KEY" | jq
```

## Testing declines

The simulated gateway forces an outcome from the payment method token prefix:

| Token prefix | Outcome |
| --- | --- |
| `pm_ok_` | Always succeeds |
| `pm_decline_` | Always declines, retriable (`insufficient_funds`) |
| `pm_expired_` | Always declines, permanent (`card_expired`) — written off immediately |
| anything else | Decided deterministically from the idempotency key |
