using PayFlow.Domain.Common;
using PayFlow.Domain.Events;
using PayFlow.Domain.Invoicing;
using Xunit;

namespace PayFlow.Domain.Tests;

public sealed class InvoiceTests
{
    private static readonly DateTimeOffset Jan1 = TestData.Jan1;

    private static Invoice Draft(string currency = "USD") =>
        new(TestData.Tenant, Guid.NewGuid(), Jan1, Jan1.AddMonths(1), currency);

    private static Invoice Open(decimal amount = 100m, TimeSpan? terms = null)
    {
        var invoice = Draft();
        invoice.AddLine("Starter plan", 1m, new Money(amount));
        invoice.Finalize(1, Jan1.AddMonths(1), terms ?? TimeSpan.Zero);
        return invoice;
    }

    [Fact]
    public void A_line_s_amount_is_derived_from_quantity_and_unit_price()
    {
        var invoice = Draft();
        invoice.AddLine("Seats", 12m, new Money(15m));

        Assert.Equal(180m, invoice.Total.Amount);
        Assert.Equal(180m, invoice.Subtotal.Amount);
    }

    [Fact]
    public void An_invoice_cannot_be_issued_with_no_lines()
    {
        Assert.Throws<DomainException>(() => Draft().Finalize(1, Jan1, TimeSpan.Zero));
    }

    [Fact]
    public void Lines_are_frozen_once_the_invoice_is_finalised()
    {
        var invoice = Open();

        // An invoice the customer has already been shown must not change under them.
        Assert.Throws<DomainException>(() => invoice.AddLine("Sneaky extra", 1m, new Money(10m)));
    }

    [Fact]
    public void A_line_in_another_currency_is_refused()
    {
        var invoice = Draft("USD");

        Assert.Throws<DomainException>(() => invoice.AddLine("Euro line", 1m, new Money(10m, "EUR")));
    }

    [Fact]
    public void Finalising_assigns_the_number_and_due_date_and_raises_an_event()
    {
        var invoice = Draft();
        invoice.AddLine("Starter plan", 1m, new Money(29m));

        invoice.Finalize(42, Jan1, TimeSpan.FromDays(30));

        Assert.Equal(42, invoice.Number);
        Assert.Equal(InvoiceStatus.Open, invoice.Status);
        Assert.Equal(Jan1.AddDays(30), invoice.DueAt);
        Assert.Contains(invoice.DomainEvents.OfType<InvoiceIssued>(), x => x.Total == 29m);
    }

    [Fact]
    public void A_draft_has_no_number_so_the_sequence_has_no_gaps()
    {
        Assert.Null(Draft().Number);
    }

    [Fact]
    public void A_zero_total_invoice_settles_on_issue_rather_than_waiting_for_a_gateway()
    {
        var invoice = Draft();
        invoice.AddLine("Metered usage within allowance", 1000m, Money.Zero());

        invoice.Finalize(1, Jan1, TimeSpan.Zero);

        // No processor accepts a zero-value charge, so leaving it Open would strand it in
        // dunning forever.
        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.True(invoice.Balance.IsZero);
    }

    [Fact]
    public void A_partial_payment_reduces_the_balance_and_leaves_the_invoice_payable()
    {
        var invoice = Open(100m);

        invoice.ApplyPayment(new Money(40m), Jan1.AddMonths(1));

        Assert.Equal(60m, invoice.Balance.Amount);
        Assert.Equal(InvoiceStatus.Open, invoice.Status);
    }

    [Fact]
    public void Paying_the_balance_in_full_closes_the_invoice()
    {
        var invoice = Open(100m);
        invoice.ApplyPayment(new Money(40m), Jan1.AddMonths(1));

        invoice.ApplyPayment(new Money(60m), Jan1.AddMonths(1).AddDays(1));

        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.True(invoice.IsFullySettled);
        Assert.Single(invoice.DomainEvents.OfType<InvoicePaid>());
    }

    [Fact]
    public void An_overpayment_leaves_a_zero_balance_rather_than_a_negative_one()
    {
        var invoice = Open(100m);

        invoice.ApplyPayment(new Money(150m), Jan1.AddMonths(1));

        Assert.True(invoice.Balance.IsZero);
    }

    [Fact]
    public void A_paid_invoice_cannot_be_paid_again_or_voided()
    {
        var invoice = Open(100m);
        invoice.ApplyPayment(new Money(100m), Jan1.AddMonths(1));

        Assert.Throws<DomainException>(() => invoice.ApplyPayment(new Money(10m), Jan1.AddMonths(1)));

        // Voiding a paid invoice would erase money that actually moved.
        Assert.Throws<DomainException>(() => invoice.Void(Jan1.AddMonths(1)));
    }

    [Fact]
    public void A_draft_invoice_cannot_be_paid()
    {
        var invoice = Draft();
        invoice.AddLine("Starter", 1m, new Money(29m));

        Assert.Throws<DomainException>(() => invoice.ApplyPayment(new Money(29m), Jan1));
    }

    [Fact]
    public void A_failed_attempt_schedules_the_next_retry_and_raises_an_event()
    {
        var invoice = Open(100m);
        var failedAt = Jan1.AddMonths(1);

        invoice.RecordFailedAttempt("insufficient_funds", failedAt, failedAt.AddDays(1));

        Assert.Equal(1, invoice.AttemptCount);
        Assert.Equal(failedAt.AddDays(1), invoice.NextAttemptAt);
        Assert.Equal(InvoiceStatus.PastDue, invoice.Status);

        var failure = Assert.Single(invoice.DomainEvents.OfType<InvoicePaymentFailed>());
        Assert.Equal("insufficient_funds", failure.FailureCode);
    }

    [Fact]
    public void An_invoice_still_inside_its_payment_terms_stays_open_after_a_failure()
    {
        var invoice = Open(100m, terms: TimeSpan.FromDays(30));

        invoice.RecordFailedAttempt("do_not_honor", Jan1.AddMonths(1), Jan1.AddMonths(1).AddDays(1));

        Assert.Equal(InvoiceStatus.Open, invoice.Status);
    }

    [Fact]
    public void Writing_an_invoice_off_clears_its_retry_and_its_balance()
    {
        var invoice = Open(100m);

        invoice.MarkUncollectible(Jan1.AddMonths(2));

        Assert.Equal(InvoiceStatus.Uncollectible, invoice.Status);
        Assert.Null(invoice.NextAttemptAt);
        Assert.True(invoice.Balance.IsZero);
    }

    [Fact]
    public void A_voided_invoice_carries_no_balance_and_is_not_collectible()
    {
        var invoice = Open(100m);

        invoice.Void(Jan1.AddMonths(1), "issued in error");

        Assert.Equal(InvoiceStatus.Void, invoice.Status);
        Assert.True(invoice.Balance.IsZero);
        Assert.False(invoice.Status.IsPayable());
        Assert.Throws<DomainException>(() => invoice.RecordFailedAttempt("x", Jan1, null));
    }

    [Fact]
    public void Reversing_a_payment_reopens_the_invoice_for_the_reversed_amount()
    {
        var invoice = Open(100m);
        invoice.ApplyPayment(new Money(100m), Jan1.AddMonths(1));

        invoice.ReversePayment(new Money(100m), Jan1.AddMonths(2));

        Assert.Equal(100m, invoice.Balance.Amount);
        Assert.Null(invoice.PaidAt);
        Assert.True(invoice.Status.IsPayable());
    }

    [Fact]
    public void More_cannot_be_reversed_than_was_paid()
    {
        var invoice = Open(100m);
        invoice.ApplyPayment(new Money(100m), Jan1.AddMonths(1));

        Assert.Throws<DomainException>(() => invoice.ReversePayment(new Money(150m), Jan1.AddMonths(2)));
    }

    [Fact]
    public void Days_overdue_counts_from_the_due_date_and_is_zero_while_current()
    {
        var invoice = Open(100m, terms: TimeSpan.FromDays(30));
        var due = invoice.DueAt!.Value;

        Assert.Equal(0, invoice.DaysOverdue(due.AddDays(-1)));
        Assert.Equal(45, invoice.DaysOverdue(due.AddDays(45)));
    }

    [Fact]
    public void A_proration_credit_is_stored_as_a_negative_line_whatever_sign_it_arrives_with()
    {
        var invoice = Draft();
        invoice.AddLine("Growth plan", 1m, new Money(300m));

        invoice.AddProrationCredit("Unused Starter time", new Money(50m));

        Assert.Equal(250m, invoice.Total.Amount);
        Assert.Contains(invoice.Lines, x => x.Kind == InvoiceLineKind.ProrationCredit && x.Amount.Amount == -50m);
    }

    [Fact]
    public void A_recurring_line_cannot_be_negative()
    {
        var invoice = Draft();

        Assert.Throws<DomainValidationException>(() =>
            invoice.AddLine("Negative charge", 1m, new Money(-10m)));
    }
}
