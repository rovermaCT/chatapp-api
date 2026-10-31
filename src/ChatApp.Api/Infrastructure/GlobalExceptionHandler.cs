using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.Api.Infrastructure;

/// <summary>
/// Turns unhandled exceptions into ProblemDetails responses. Expected failures go through
/// <see cref="Services.ServiceResult{T}"/>; this only sees exceptions nothing else handled.
/// </summary>
internal sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetails,
    IHostEnvironment environment,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    // Non-standard (nginx) code for "client closed request"; nobody is listening for the response anyway.
    private const int ClientClosedRequest = 499;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var request = httpContext.Request;
        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogInformation("Request {Method} {Path} was cancelled by the client", request.Method, request.Path);
            httpContext.Response.StatusCode = ClientClosedRequest;
            return true;
        }

        var (statusCode, title, safeDetail) = Map(exception);

        logger.Log(statusCode >= StatusCodes.Status500InternalServerError ? LogLevel.Error : LogLevel.Warning, exception,
            "Unhandled exception for {Method} {Path} mapped to {StatusCode} (trace {TraceId})",
            request.Method, request.Path, statusCode, traceId);

        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            // Raw exception messages can carry SQL, paths or other internals, so they are shown in Development only.
            Detail = environment.IsDevelopment() ? exception.Message : safeDetail
        };
        // traceId is added by the ProblemDetails defaults, so it matches the TraceId logged above.
        if (environment.IsDevelopment())
        {
            problem.Extensions["exception"] = new
            {
                type = exception.GetType().FullName,
                message = exception.Message,
                stackTrace = exception.StackTrace
            };
        }

        httpContext.Response.StatusCode = statusCode;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception
        });
    }

    private static (int StatusCode, string Title, string Detail) Map(Exception exception) => exception switch
    {
        BadHttpRequestException bad => (bad.StatusCode, "Bad request", "The request could not be processed."),
        ArgumentException or FormatException => (StatusCodes.Status400BadRequest, "Bad request", "The request contained invalid data."),
        UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "Forbidden", "You do not have access to this resource."),
        KeyNotFoundException => (StatusCodes.Status404NotFound, "Not found", "The requested resource was not found."),
        // DbUpdateConcurrencyException derives from DbUpdateException, so one arm covers both.
        DbUpdateException => (StatusCodes.Status409Conflict, "Conflict", "The request conflicts with existing data. Reload and try again."),
        TimeoutException => (StatusCodes.Status504GatewayTimeout, "Gateway timeout", "The operation timed out. Try again later."),
        NotImplementedException => (StatusCodes.Status501NotImplemented, "Not implemented", "This operation is not implemented."),
        _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred", "An unexpected error occurred. Quote the traceId when reporting it.")
    };
}
