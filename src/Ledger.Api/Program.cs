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

        // GET /health returns 200 only when database is reachable, otherwise 503
        app.MapGet("/health", async () =>
            await Ledger.PingAsync(db)
                ? Results.Ok(new HealthResponse("ok"))
                : ProblemResult(Problem.DatabaseUnavailable()));

        await app.RunAsync();
    }
}

internal sealed record HealthResponse(string Status);