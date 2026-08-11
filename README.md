# Double-entry ledger

An HTTP API for moving money between accounts, written in C# on .NET 10 with PostgreSQL.

Money is never edited in place: every movement writes an immutable transfer plus two equal and opposite postings, so the sum of all postings in the database is always zero and any balance can be explained by the entries that produced it. Balances cannot go negative, retried requests cannot apply twice, and concurrent requests against the same account cannot lose or duplicate money. Amounts are always integer minor units (cents), never floats or formatted strings.

## Endpoints

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/health` | `200` when the service can reach the database, `503` when it cannot |
| `POST` | `/accounts` | Create an account with an exact opening balance |
| `GET` | `/accounts/{id}/balance` | Read an account's current balance |
| `POST` | `/transfers` | Move money between two accounts. Requires an `Idempotency-Key` header |
| `GET` | `/transfers/{id}` | Read a transfer |

## Run it

Needs Docker and .NET SDK 10.0.302 (pinned in `global.json`).

```powershell
docker compose up -d --wait
dotnet run --project src/Ledger.Api
```

The launch profile supplies `DATABASE_URL` and the port. The API listens on `http://localhost:5290`, and the schema is created on startup.

Check it is up from a second terminal:

```powershell
Invoke-RestMethod http://localhost:5290/health

# status
# ------
# ok
```

## Using it

Open two accounts. You supply the account UUID, which is what makes creation safe to retry.

```powershell
$base  = 'http://localhost:5290'
$alice = [guid]::NewGuid().ToString()
$bob   = [guid]::NewGuid().ToString()

$body = @{ id = $alice; currency = 'USD'; openingBalanceMinor = 10000 } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "$base/accounts" -ContentType 'application/json' -Body $body

$body = @{ id = $bob; currency = 'USD'; openingBalanceMinor = 0 } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "$base/accounts" -ContentType 'application/json' -Body $body
```

Sending either request again unchanged returns `200` and does not open a second account. Sending it with a different currency or opening balance under the same UUID returns `409 /problems/account-conflict`.

Move $25.00. The `Idempotency-Key` header is required, and is 1 to 128 visible ASCII characters.

```powershell
$body = @{ sourceAccountId = $alice; destinationAccountId = $bob; amountMinor = 2500 } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "$base/transfers" `
    -ContentType 'application/json' `
    -Headers @{ 'Idempotency-Key' = 'demo-key-1' } `
    -Body $body

# id                  : ed7878e8-5a9b-4818-a2a0-f9b5365d4d5b
# type                : transfer
# sourceAccountId     : 3ac80a6c-80ee-47af-bbd6-e24a66561be1
# destinationAccountId: 5fc3bde1-3601-433b-9b61-f9e8a0c136a4
# amountMinor         : 2500
# currency            : USD
# reversalOf          :
```

Repeat that call with the same key and body and you get `200` with the same transfer id, and the balances do not move again. Reuse the key with a changed amount and you get `409 /problems/idempotency-conflict`. A transfer that fails a rule does not consume its key, so you can correct the problem and retry under the same one.

Read a balance:

```powershell
Invoke-RestMethod "$base/accounts/$alice/balance"

# id                                   currency balanceMinor
# --                                   -------- ------------
# 3ac80a6c-80ee-47af-bbd6-e24a66561be1 USD              7500
```

`Invoke-RestMethod` throws on any `4xx` or `5xx`, so read the problem body from the exception when you want to see a rejection:

```powershell
try {
    $body = @{ sourceAccountId = $alice; destinationAccountId = $bob; amountMinor = 999999 } | ConvertTo-Json
    Invoke-RestMethod -Method Post -Uri "$base/transfers" `
        -ContentType 'application/json' `
        -Headers @{ 'Idempotency-Key' = 'demo-key-2' } `
        -Body $body
}
catch {
    $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
    $reader.ReadToEnd()
}

# {"type":"/problems/insufficient-funds","title":"Insufficient funds","status":422,
#  "detail":"The requested account has insufficient funds"}
```

## Errors

Every rejection is `application/problem+json` (RFC 9457).

```json
{
  "type": "/problems/insufficient-funds",
  "title": "Insufficient funds",
  "status": 422,
  "detail": "The requested account has insufficient funds"
}
```

| Status | Types |
|---:|---|
| 400 | `/problems/invalid-request` |
| 404 | `/problems/account-not-found`, `/problems/transfer-not-found` |
| 409 | `/problems/account-conflict`, `/problems/idempotency-conflict` |
| 422 | `/problems/currency-mismatch`, `/problems/insufficient-funds`, `/problems/balance-limit-exceeded` |
| 503 | `/problems/database-unavailable` |

## How it stays correct

- Both account rows are locked with `SELECT ... FOR UPDATE` ordered by account id, so transfers in opposing directions between the same pair cannot deadlock.
- The balance checks run while those locks are held, in the same transaction as the write.
- The idempotency key is claimed in that same transaction, so the key and the movement commit or roll back together.
- Opening balances come from a hidden system equity account per currency rather than appearing from nowhere. It is not reachable through the API.
- The database enforces the rules independently of the application code: a `CHECK` keeps user balances within `0 .. 2^63-1`, postings are unique per transfer and account, and transfers are insert-only.

## Build and test

```powershell
dotnet build Ledger.slnx -c Release
dotnet test Ledger.slnx
```
