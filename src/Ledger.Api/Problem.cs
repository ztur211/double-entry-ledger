// Problem class represents an HTTP problem response with a status code, error code, and title
internal sealed class Problem(int status, string code, string title) : Exception(title)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string Title => Message;
    public static Problem Invalid(string title) =>
        new(400, "invalid_request", title);
    public static Problem AccountNotFound() =>
        new(404, "account_not_found", "Account not found");
    public static Problem TransferNotFound() =>
        new(404, "transfer_not_found", "Transfer not found");
    public static Problem AccountConflict() =>
        new(409, "account_conflict", "Account ID already exists");
    public static Problem IdempotencyConflict() =>
        new(409, "idempotency_conflict", "Idempotency key was already used for a different request");
    public static Problem AlreadyReversed() =>
        new(409, "already_reversed", "A transfer can only be reversed once");
    public static Problem NotReversible() =>
        new(409, "not_reversible", "Transfer is not reversible");
    public static Problem CurrencyMismatch() =>
        new(422, "currency_mismatch", "Currency mismatch");
    public static Problem InsufficientFunds() =>
        new(422, "insufficient_funds", "Account has insufficient funds");
    public static Problem BalanceLimit() =>
        new(422, "balance_limit_exceeded", "Account balance limit exceeded");
    public static Problem DatabaseUnavailable() =>
        new(503, "database_unavailable", "Database is unavailable");
}