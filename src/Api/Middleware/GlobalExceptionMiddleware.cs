using EventWOS.Shared.Common;
using FluentValidation;
using System.Text.Json;

namespace EventWOS.Api.Middleware;

/// <summary>
/// Global unhandled exception handler. Catches all exceptions and returns
/// a structured ApiResponse with appropriate HTTP status codes.
/// </summary>
public sealed class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;
    private readonly IHostEnvironment _env;

    public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger, IHostEnvironment env)
    {
        _next = next;
        _logger = logger;
        _env = env;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (ValidationException ex)
        {
            _logger.LogWarning("Validation failed: {Errors}", ex.Errors.Select(e => e.ErrorMessage));
            await WriteErrorResponseAsync(context, 400,
                ex.Errors.Select(e => e.ErrorMessage).ToList(),
                context.TraceIdentifier);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning("Unauthorized: {Message}", ex.Message);
            await WriteErrorResponseAsync(context, 403, new[] { "Forbidden." }, context.TraceIdentifier);
        }
        catch (KeyNotFoundException ex)
        {
            await WriteErrorResponseAsync(context, 404, new[] { ex.Message }, context.TraceIdentifier);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unhandled exception [{ExType}] for {Method} {Path} — {Message}",
                ex.GetType().Name,
                context.Request.Method,
                context.Request.Path,
                ex.Message);

            // Surface exception type + message in the response (no stack trace).
            // Helps diagnose prod issues without exposing internals.
            var inner = ex.InnerException is not null
                ? $" → {ex.InnerException.GetType().Name}: {ex.InnerException.Message}"
                : "";
            var message = $"{ex.GetType().Name}: {ex.Message}{inner}";

            await WriteErrorResponseAsync(context, 500, new[] { message }, context.TraceIdentifier);
        }
    }

    private static async Task WriteErrorResponseAsync(
        HttpContext context, int statusCode,
        IEnumerable<string> errors, string traceId)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        var response = ApiResponse<object>.Fail(errors.ToList(), traceId);
        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await context.Response.WriteAsync(json);
    }
}
