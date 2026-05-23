namespace EventWOS.Shared.Common;

/// <summary>Standardised API envelope. All responses use this shape.</summary>
public sealed class ApiResponse<T>
{
    public bool Success { get; init; }
    public T? Data { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = new List<string>();
    public string? TraceId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    public static ApiResponse<T> Ok(T data, string? message = null) =>
        new() { Success = true, Data = data, Message = message };

    public static ApiResponse<T> Fail(string error, string? traceId = null) =>
        new() { Success = false, Errors = new[] { error }, TraceId = traceId };

    public static ApiResponse<T> Fail(IReadOnlyList<string> errors, string? traceId = null) =>
        new() { Success = false, Errors = errors, TraceId = traceId };
}

public sealed class ApiResponse
{
    public bool Success { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = new List<string>();
    public string? TraceId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    public static ApiResponse Ok(string? message = null) =>
        new() { Success = true, Message = message };

    public static ApiResponse Fail(string error, string? traceId = null) =>
        new() { Success = false, Errors = new[] { error }, TraceId = traceId };
}
