using System.Text.Json;
using Npgsql;

namespace Ledger.Api;

public class Program
{
    public static async Task Main(string[] args)
    {
        string connectionString = Environment.GetEnvironmentVariable("DATABASE_URL") ?? throw new InvalidOperationException("Set the DATABASE_URL environment variable");

        var builder = WebApplication.CreateBuilder(args);

        // Ensure formatting of property names in JSON responses use camelCase
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.RespectRequiredConstructorParameters = true;
        });
        // Throw an exception if a bad request is received, instead of returning a 400 response with no body
        builder.Services.Configure<RouteHandlerOptions>(options =>
        {
            options.ThrowOnBadRequest = true;
        });

        var app = builder.Build();

        await using var db = NpgsqlDataSource.Create(connectionString);

        // Run once during startup before requests hit API
        await Ledger.EnsureSchemaAsync(db);

        // Endpoints and Ledger methods to throw Problem
        app.Use(async (context, next) =>
        {
            try
            {
                if (RequiresJsonBody(context.Request) && !HasUtf8JsonContentType(context.Request))
                {
                    throw Problem.Invalid("Content-Type must be application/json with UTF-8 encoding");
                }

                await next(context);

                // Invalid request headers 400 /problems/invalid-request
                if (context.Response.StatusCode == StatusCodes.Status415UnsupportedMediaType && !context.Response.HasStarted)
                {
                    context.Response.Clear();
                    await ProblemResult(Problem.Invalid("Content-Type must be application/json")).ExecuteAsync(context);
                }
            }
            catch (Problem problem)
            {
                await ProblemResult(problem).ExecuteAsync(context);
            }
            catch (BadHttpRequestException)
            {
                // Bad JSON format
                await ProblemResult(Problem.Invalid("Request body is invalid")).ExecuteAsync(context);
            }
        });

        // Define how failures are displayed, refer RFC 9457
        static IResult ProblemResult(Problem problem) =>
            Results.Json(
                new
                {
                    problem.Type,
                    problem.Title,
                    problem.Status,
                    problem.Detail
                },
                statusCode: problem.Status,
                contentType: "application/problem+json");

        // Determine if the request requires a JSON body
        static bool RequiresJsonBody(HttpRequest request)
        {
            string path = request.Path.Value?.TrimEnd('/') ?? string.Empty;
            return HttpMethods.IsPost(request.Method) && (
                string.Equals(path, "/accounts", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, "/transfers", StringComparison.OrdinalIgnoreCase));
        }

        // Determine if the request has a valid UTF-8 JSON content type
        static bool HasUtf8JsonContentType(HttpRequest request)
        {
            if (!Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(
                    request.ContentType,
                    out Microsoft.Net.Http.Headers.MediaTypeHeaderValue? contentType) ||
                !string.Equals(
                    contentType.MediaType.Value,
                    "application/json",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return !contentType.Charset.HasValue ||
                string.Equals(
                    contentType.Charset.Value,
                    "utf-8",
                    StringComparison.OrdinalIgnoreCase);
        }

        // API endpoints

        // GET /health
        // Return 200 when database is available
        // Return 503 when database is unavailable
        app.MapGet("/health", async () =>
            await Ledger.PingAsync(db)
                ? Results.Ok(new HealthResponse("ok"))
                : ProblemResult(Problem.DatabaseUnavailable()));

        // POST /accounts creates a new account, handles identical retry
        // Return 201 for new account, 200 if account already exists and is identical
        // Return 400 if request body is invalid, 409 if account already exists and is not identical
        app.MapPost("/accounts", async (CreateAccountRequest request) =>
        {
            if (request.Id == Guid.Empty || !IsCurrency(request.Currency) || request.OpeningBalanceMinor < 0)
            {
                throw Problem.Invalid("ID, currency, or opening balance is invalid");
            }
            var (account, created) = await Ledger.CreateAccountAsync(db, request.Id, request.Currency!, request.OpeningBalanceMinor);
            return created
                ? Results.Created($"/accounts/{account.Id:D}/balance", account)
                : Results.Ok(account);
        });

        // GET /accounts/{id}/balance returns the current balance of an account
        // Return 200 with balance details
        // Return 400 for invalid ID, 404 if account does not exist
        app.MapGet("/accounts/{id}/balance", async (string id) =>
            Results.Ok(await Ledger.GetBalanceAsync(db, ParseId(id, "Account"))));

        // Parse non-empty GUID from string
        // Return 400 for invalid ID
        static Guid ParseId(string value, string subject) =>
            Guid.TryParse(value, out var id) && id != Guid.Empty
                ? id
                : throw Problem.Invalid($"{subject} ID is invalid");

        // Accept exactly three uppercase ASCII letters for currency code
        static bool IsCurrency(string? currency) =>
            currency is { Length: 3 } && currency.All(letter => letter is >= 'A' and <= 'Z');

        await app.RunAsync();
    }
}

internal sealed record HealthResponse(string Status);
internal sealed record CreateAccountRequest(Guid Id, string? Currency, long OpeningBalanceMinor);
internal sealed record AccountResponse(Guid Id, string Currency, long OpeningBalanceMinor);
internal sealed record BalanceResponse(Guid Id, string Currency, long BalanceMinor);