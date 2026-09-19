# Analytics

`payflow_analytics.ipynb` reads the live PayFlow PostgreSQL database and reports on
what the billing engine actually did.

## Running it

```bash
# 1. Database up
docker compose up -d postgres

# 2. Schema and a year of history. The seeder drives the real billing cycle against a
#    clock it controls, so every invoice, retry and write-off is the engine's own output.
dotnet run --project src/PayFlow.Seeder -- --reset --months 12

# 3. Notebook
pip install -r analytics/requirements.txt
jupyter notebook analytics/payflow_analytics.ipynb
```

## Configuration

Read from the environment, with defaults matching `docker-compose.yml`:

| Variable | Default | Purpose |
|---|---|---|
| `PAYFLOW_DATABASE_URL` | `postgresql+psycopg2://payflow:payflow@localhost:55433/payflow` | Connection string |
| `PAYFLOW_TENANT_ID` | `11111111-…-111111111111` | Which tenant to analyse |
| `PAYFLOW_CHART_MODE` | `light` | `light` or `dark` chart palette |

## What it covers

| Section | Question it answers |
|---|---|
| Headline | MRR, ARR, collected, written off, realisation rate |
| Recurring revenue | MRR month by month, and which plans carry it |
| Revenue realisation | Of everything invoiced, how much was collected, is outstanding, or was written off |
| Dunning recovery | Recovery rate at each attempt in the retry schedule |
| Receivables aging | Outstanding balance by days past due |
| Cohort retention | Retention triangle by signup month |
| Decline reasons | Why cards fail, split into retriable and permanent |
| Tenant isolation | Asserts no cross-tenant references and gap-free per-tenant invoice numbering |

## Notes

- **Nothing is synthesised in the notebook.** Every figure comes from rows the billing
  engine wrote, so a wrong curve means a bug in the billing code rather than in the
  chart.
- **The last two cells are assertions, not illustrations.** They query underneath the
  application - straight to PostgreSQL, bypassing the EF Core query filters - to check
  that tenant isolation actually holds and that invoice sequences have no gaps. If the
  notebook runs to completion, both held.
- **MRR excludes trials.** A trial is worth nothing until it converts; counting trials
  is the usual way a SaaS dashboard overstates itself.
- Charts use a colour-vision-validated palette, one y-axis per chart, and never rely on
  colour alone to carry meaning.
