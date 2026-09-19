namespace PayFlow.Domain.Common;

/// <summary>
/// Raised when an operation would leave an aggregate in a state the business rules
/// forbid — an illegal subscription transition, paying a voided invoice, mixing
/// currencies. The API layer maps this to HTTP 409, and <see cref="DomainValidationException"/>
/// to HTTP 400, so callers can tell "you sent nonsense" from "that is not allowed right now".
/// </summary>
public class DomainException : Exception
{
    public DomainException(string message) : base(message) { }

    public DomainException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Raised when a value supplied to a constructor or method is not acceptable.</summary>
public sealed class DomainValidationException : DomainException
{
    public DomainValidationException(string message) : base(message) { }

    public DomainValidationException(string parameter, string message) : base(message) => Parameter = parameter;

    public string? Parameter { get; }
}
