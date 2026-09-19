using System.Globalization;
using System.Net;
using System.Text;
using PayFlow.Application.Abstractions;
using PayFlow.Application.Analytics;
using PayFlow.Application.Customers;
using PayFlow.Application.Invoicing;
using PayFlow.Application.Plans;
using PayFlow.Application.Subscriptions;

namespace PayFlow.Web.Dashboard;

/// <summary>
/// A server-rendered operations view over the same services the API uses.
/// <para>
/// The page reads through the application layer rather than touching the DbContext, so
/// what it shows is what the API returns - the earlier version queried the database
/// directly and drifted from the API's own view of the data.
/// </para>
/// <para>
/// It is authenticated by the same API key middleware as everything else. In the
/// Development environment anonymous access resolves to the development tenant, which is
/// what makes it usable from a browser without pasting a header.
/// </para>
/// </summary>
public static class DashboardEndpoint
{
    public static IEndpointRouteBuilder MapDashboard(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/", () => Results.Redirect("/dashboard")).ExcludeFromDescription();

        app.MapGet("/dashboard", async (
            IAnalyticsService analytics,
            ISubscriptionService subscriptions,
            IInvoiceService invoices,
            IPlanService plans,
            ICustomerService customers,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var page = PageRequest.Of(1, 10);

            var mrr = await analytics.GetMrrSnapshotAsync(cancellationToken);
            var aging = await analytics.GetInvoiceAgingAsync(cancellationToken);
            var funnel = await analytics.GetDunningFunnelAsync(cancellationToken);
            var declines = await analytics.GetDeclineReasonsAsync(cancellationToken);
            var recentSubscriptions = await subscriptions.ListAsync(page, cancellationToken: cancellationToken);
            var recentInvoices = await invoices.ListAsync(page, cancellationToken: cancellationToken);
            var planList = await plans.ListAsync(cancellationToken: cancellationToken);
            var customerList = await customers.ListAsync(PageRequest.Of(1, 100), cancellationToken: cancellationToken);

            var tenantName = context.Items["TenantName"] as string ?? "Unknown tenant";

            var html = Render(
                tenantName, mrr, aging, funnel, declines,
                recentSubscriptions.Items, recentInvoices.Items, planList, customerList.Items);

            return Results.Content(html, "text/html; charset=utf-8");
        })
        .ExcludeFromDescription();

        return app;
    }

    private static string Render(
        string tenantName,
        MrrSnapshotDto mrr,
        IReadOnlyList<InvoiceAgingBucketDto> aging,
        IReadOnlyList<DunningFunnelStepDto> funnel,
        IReadOnlyList<DeclineReasonDto> declines,
        IReadOnlyList<SubscriptionDto> subscriptions,
        IReadOnlyList<InvoiceDto> invoices,
        IReadOnlyList<PlanDto> plans,
        IReadOnlyList<CustomerDto> customers)
    {
        var sb = new StringBuilder(16_384);

        sb.Append(
            """
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>PayFlow</title>
            <style>
              :root {
                --bg: #f6f7f9; --card: #ffffff; --ink: #16202c; --muted: #5d6b7a;
                --line: #dfe4ea; --accent: #2f6df6; --good: #17803d; --warn: #b45309; --bad: #b42318;
              }
              @media (prefers-color-scheme: dark) {
                :root {
                  --bg: #12161c; --card: #1a2029; --ink: #e8edf3; --muted: #96a3b2;
                  --line: #2b333f; --accent: #6b9bff; --good: #3fb97e; --warn: #f0b429; --bad: #f87171;
                }
              }
              * { box-sizing: border-box; }
              body { margin: 0; background: var(--bg); color: var(--ink);
                     font: 15px/1.5 ui-sans-serif, system-ui, -apple-system, "Segoe UI", sans-serif; }
              .wrap { max-width: 1180px; margin: 0 auto; padding: 24px 16px 64px; }
              h1 { font-size: 22px; margin: 0; }
              h2 { font-size: 15px; margin: 0 0 12px; letter-spacing: .02em;
                   text-transform: uppercase; color: var(--muted); }
              header { display: flex; justify-content: space-between; align-items: baseline;
                       flex-wrap: wrap; gap: 8px; margin-bottom: 20px; }
              .sub { color: var(--muted); font-size: 13px; }
              .grid { display: grid; gap: 16px; }
              .tiles { grid-template-columns: repeat(auto-fit, minmax(170px, 1fr)); margin-bottom: 20px; }
              .two { grid-template-columns: repeat(auto-fit, minmax(340px, 1fr)); }
              .card { background: var(--card); border: 1px solid var(--line);
                      border-radius: 10px; padding: 16px; }
              .tile .value { font-size: 26px; font-weight: 600; font-variant-numeric: tabular-nums; }
              .tile .label { color: var(--muted); font-size: 12px; text-transform: uppercase;
                             letter-spacing: .04em; margin-bottom: 6px; }
              table { width: 100%; border-collapse: collapse; font-size: 13.5px; }
              th { text-align: left; color: var(--muted); font-weight: 600; font-size: 11.5px;
                   text-transform: uppercase; letter-spacing: .04em; padding: 6px 8px;
                   border-bottom: 1px solid var(--line); }
              td { padding: 7px 8px; border-bottom: 1px solid var(--line); }
              tr:last-child td { border-bottom: none; }
              .num { text-align: right; font-variant-numeric: tabular-nums; }
              .pill { display: inline-block; padding: 1px 8px; border-radius: 999px;
                      font-size: 11.5px; font-weight: 600; border: 1px solid currentColor; }
              .ok { color: var(--good); } .warn { color: var(--warn); } .bad { color: var(--bad); }
              .dim { color: var(--muted); }
              .bar { height: 7px; border-radius: 4px; background: var(--accent); min-width: 2px; }
              .track { background: var(--line); border-radius: 4px; height: 7px; }
              .empty { color: var(--muted); font-style: italic; padding: 8px; }
              code { background: var(--bg); padding: 1px 5px; border-radius: 4px; font-size: 12.5px; }
            </style></head><body><div class="wrap">
            """);

        sb.Append("<header><div><h1>PayFlow</h1><div class=\"sub\">")
          .Append(E(tenantName))
          .Append(" &middot; multi-tenant subscription billing</div></div>")
          .Append("<div class=\"sub\">API docs at <code>/openapi/v1.json</code> &middot; metrics at <code>/metrics</code></div></header>");

        // Headline tiles.
        sb.Append("<div class=\"grid tiles\">");
        Tile(sb, "Monthly recurring revenue", Currency(mrr.MonthlyRecurringRevenue, mrr.Currency));
        Tile(sb, "Annual run rate", Currency(mrr.AnnualRunRate, mrr.Currency));
        Tile(sb, "Active", mrr.ActiveSubscriptions.ToString(CultureInfo.InvariantCulture));
        Tile(sb, "Trialing", mrr.TrialingSubscriptions.ToString(CultureInfo.InvariantCulture));
        Tile(sb, "Past due", mrr.PastDueSubscriptions.ToString(CultureInfo.InvariantCulture),
            mrr.PastDueSubscriptions > 0 ? "bad" : null);
        Tile(sb, "ARPA", Currency(mrr.AverageRevenuePerAccount, mrr.Currency));
        sb.Append("</div>");

        sb.Append("<div class=\"grid two\">");

        // MRR by plan, with a proportional bar so the mix is readable at a glance.
        sb.Append("<section class=\"card\"><h2>MRR by plan</h2>");
        if (mrr.ByPlan.Count == 0)
        {
            sb.Append("<div class=\"empty\">No billable subscriptions yet.</div>");
        }
        else
        {
            var max = mrr.ByPlan.Max(x => x.MonthlyRecurringRevenue);
            sb.Append("<table><tr><th>Plan</th><th class=\"num\">Subs</th><th class=\"num\">MRR</th><th style=\"width:34%\"></th></tr>");
            foreach (var plan in mrr.ByPlan)
            {
                var width = max <= 0 ? 0 : (int)Math.Round(plan.MonthlyRecurringRevenue / max * 100m);
                sb.Append("<tr><td><strong>").Append(E(plan.PlanCode)).Append("</strong></td>")
                  .Append("<td class=\"num\">").Append(plan.Subscriptions).Append("</td>")
                  .Append("<td class=\"num\">").Append(E(Currency(plan.MonthlyRecurringRevenue, mrr.Currency))).Append("</td>")
                  .Append("<td><div class=\"track\"><div class=\"bar\" style=\"width:")
                  .Append(width).Append("%\"></div></div></td></tr>");
            }

            sb.Append("</table>");
        }

        sb.Append("</section>");

        // Receivables aging.
        sb.Append("<section class=\"card\"><h2>Invoice aging</h2>");
        if (aging.All(x => x.InvoiceCount == 0))
        {
            sb.Append("<div class=\"empty\">Nothing outstanding.</div>");
        }
        else
        {
            sb.Append("<table><tr><th>Bucket</th><th class=\"num\">Invoices</th><th class=\"num\">Balance</th></tr>");
            foreach (var bucket in aging)
            {
                var cls = bucket.Bucket == "current" ? "ok" : bucket.Bucket == "90+" ? "bad" : "warn";
                sb.Append("<tr><td><span class=\"pill ").Append(cls).Append("\">")
                  .Append(E(bucket.Bucket)).Append("</span></td>")
                  .Append("<td class=\"num\">").Append(bucket.InvoiceCount).Append("</td>")
                  .Append("<td class=\"num\">").Append(E(Currency(bucket.Balance, mrr.Currency))).Append("</td></tr>");
            }

            sb.Append("</table>");
        }

        sb.Append("</section>");

        // Dunning recovery by attempt.
        sb.Append("<section class=\"card\"><h2>Dunning recovery</h2>");
        if (funnel.Count == 0)
        {
            sb.Append("<div class=\"empty\">No collection attempts yet.</div>");
        }
        else
        {
            sb.Append("<table><tr><th>Attempt</th><th class=\"num\">Tried</th><th class=\"num\">Recovered</th><th class=\"num\">Rate</th><th style=\"width:26%\"></th></tr>");
            foreach (var step in funnel)
            {
                var pct = (int)Math.Round(step.RecoveryRate * 100);
                sb.Append("<tr><td>#").Append(step.AttemptNumber).Append("</td>")
                  .Append("<td class=\"num\">").Append(step.Attempts).Append("</td>")
                  .Append("<td class=\"num\">").Append(step.Recovered).Append("</td>")
                  .Append("<td class=\"num\">").Append(pct).Append("%</td>")
                  .Append("<td><div class=\"track\"><div class=\"bar\" style=\"width:")
                  .Append(pct).Append("%\"></div></div></td></tr>");
            }

            sb.Append("</table>");
        }

        sb.Append("</section>");

        // Why cards are failing.
        sb.Append("<section class=\"card\"><h2>Decline reasons</h2>");
        if (declines.Count == 0)
        {
            sb.Append("<div class=\"empty\">No declines recorded.</div>");
        }
        else
        {
            sb.Append("<table><tr><th>Reason</th><th class=\"num\">Count</th><th class=\"num\">Share</th></tr>");
            foreach (var decline in declines)
            {
                sb.Append("<tr><td><code>").Append(E(decline.FailureCode)).Append("</code></td>")
                  .Append("<td class=\"num\">").Append(decline.Count).Append("</td>")
                  .Append("<td class=\"num\">").Append((int)Math.Round(decline.Share * 100)).Append("%</td></tr>");
            }

            sb.Append("</table>");
        }

        sb.Append("</section>");

        // Recent subscriptions.
        sb.Append("<section class=\"card\"><h2>Recent subscriptions</h2>");
        if (subscriptions.Count == 0)
        {
            sb.Append("<div class=\"empty\">No subscriptions yet.</div>");
        }
        else
        {
            sb.Append("<table><tr><th>Customer</th><th>Plan</th><th>Status</th><th class=\"num\">MRR</th><th>Period ends</th></tr>");
            foreach (var subscription in subscriptions)
            {
                sb.Append("<tr><td>").Append(E(subscription.CustomerEmail)).Append("</td>")
                  .Append("<td>").Append(E(subscription.PlanCode))
                  .Append(subscription.Quantity > 1 ? $" <span class=\"dim\">&times;{subscription.Quantity}</span>" : "")
                  .Append("</td><td><span class=\"pill ").Append(StatusClass(subscription.Status.ToString())).Append("\">")
                  .Append(E(subscription.Status.ToString())).Append("</span>")
                  .Append(subscription.CancelAtPeriodEnd ? " <span class=\"dim\">ending</span>" : "")
                  .Append("</td><td class=\"num\">")
                  .Append(E(Currency(subscription.MonthlyRecurringRevenue, subscription.Currency)))
                  .Append("</td><td class=\"dim\">")
                  .Append(subscription.PeriodEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                  .Append("</td></tr>");
            }

            sb.Append("</table>");
        }

        sb.Append("</section>");

        // Recent invoices.
        sb.Append("<section class=\"card\"><h2>Recent invoices</h2>");
        if (invoices.Count == 0)
        {
            sb.Append("<div class=\"empty\">No invoices issued yet.</div>");
        }
        else
        {
            sb.Append("<table><tr><th>No.</th><th>Status</th><th class=\"num\">Total</th><th class=\"num\">Balance</th><th class=\"num\">Tries</th></tr>");
            foreach (var invoice in invoices)
            {
                sb.Append("<tr><td>").Append(invoice.Number?.ToString(CultureInfo.InvariantCulture) ?? "<span class=\"dim\">draft</span>")
                  .Append("</td><td><span class=\"pill ").Append(StatusClass(invoice.Status.ToString())).Append("\">")
                  .Append(E(invoice.Status.ToString())).Append("</span></td>")
                  .Append("<td class=\"num\">").Append(E(Currency(invoice.Total, invoice.Currency))).Append("</td>")
                  .Append("<td class=\"num\">").Append(E(Currency(invoice.Balance, invoice.Currency))).Append("</td>")
                  .Append("<td class=\"num\">").Append(invoice.AttemptCount).Append("</td></tr>");
            }

            sb.Append("</table>");
        }

        sb.Append("</section>");

        sb.Append("</div>");

        sb.Append("<p class=\"sub\" style=\"margin-top:20px\">")
          .Append(plans.Count).Append(" plans &middot; ")
          .Append(customers.Count).Append(" customers loaded. Run a billing cycle with ")
          .Append("<code>POST /v1/billing/run-cycle</code>.</p>");

        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    private static void Tile(StringBuilder sb, string label, string value, string? cssClass = null)
    {
        sb.Append("<div class=\"card tile\"><div class=\"label\">").Append(E(label)).Append("</div>")
          .Append("<div class=\"value ").Append(cssClass ?? "").Append("\">").Append(E(value)).Append("</div></div>");
    }

    private static string StatusClass(string status) => status switch
    {
        "Active" or "Paid" => "ok",
        "Trialing" or "Open" or "Draft" or "Paused" => "",
        "PastDue" => "warn",
        _ => "bad",
    };

    private static string Currency(decimal amount, string currency) =>
        string.Create(CultureInfo.InvariantCulture, $"{amount:N2} {currency}");

    /// <summary>
    /// HTML-encodes untrusted text. Customer-supplied names and emails are rendered on
    /// this page, so every interpolated value goes through here.
    /// </summary>
    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
}
