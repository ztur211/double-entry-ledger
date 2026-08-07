namespace Ledger.Api;

// Problem class represents an HTTP problem response with a status code, error code, and title
internal sealed class Problem(int status, string type, string title, string detail) : Exception(detail)
{
    public int Status { get; } = status;
    public string Type { get; } = type;
    public string Title { get; } = title;
    public string Detail => Message;
    public static Problem Invalid(string detail) =>
        new(
            400,
            "/problems/invalid-request",
            "Invalid request",
            detail);
    public static Problem AccountNotFound() =>
        new(
            404,
            "/problems/account-not-found",
            "Account not found",
            "The requested account does not exist");
    public static Problem TransferNotFound() =>
        new(404,
            "/problems/transfer-not-found",
            "Transfer not found",
            "The requested transfer does not exist");
    public static Problem AccountConflict() =>
        new(409,
            "/problems/account-conflict",
            "Account conflict",
            "An account with this ID already exists");
    public static Problem IdempotencyConflict() =>
        new(409,
            "/problems/idempotency-conflict",
            "Idempotency conflict",
            "The provided idempotency key has already been used for a different request");
    public static Problem AlreadyReversed() =>
        new(409,
            "/problems/already-reversed",
            "Transfer already reversed",
            "The requested transfer has already been reversed");
    public static Problem NotReversible() =>
        new(409,
            "/problems/not-reversible",
            "Transfer is not reversible",
            "The requested transfer cannot be reversed");
    public static Problem CurrencyMismatch() =>
        new(422,
            "/problems/currency-mismatch",
            "Currency mismatch",
            "The currencies of the source and destination accounts do not match");
    public static Problem InsufficientFunds() =>
        new(422,
            "/problems/insufficient-funds",
            "Insufficient funds",
            "The requested account has insufficient funds");
    public static Problem BalanceLimit() =>
        new(422,
            "/problems/balance-limit-exceeded",
            "Balance limit exceeded",
            "The requested account has exceeded its balance limit");
    public static Problem DatabaseUnavailable() =>
        new(503,
            "/problems/database-unavailable",
            "Database unavailable",
            "The ledger database cannot be reached");
}