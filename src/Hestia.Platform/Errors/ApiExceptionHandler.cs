using Hestia.Platform.Exceptions;
using Hestia.Platform.Finance;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Hestia.Platform.Errors;

/// <summary>
/// Turns exceptions into RFC 9457 problem details. Platform exceptions become 400/404/409 with
/// their message. A failed finance call (<see cref="FinanceRequestException"/>) is logged and becomes 502.
/// Other exceptions are logged and returned as a generic 500.
/// </summary>
public class ApiExceptionHandler(IProblemDetailsService problemDetails, ILogger<ApiExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is InvalidInputException invalid)
        {
            // The same shape as model validation, so clients show the message next to the field.
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return await problemDetails.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                Exception = exception,
                ProblemDetails = new HttpValidationProblemDetails(
                    new Dictionary<string, string[]> { [invalid.Field] = [exception.Message] })
                {
                    Status = StatusCodes.Status400BadRequest
                }
            });
        }

        var (status, title, detail) = exception switch
        {
            NotFoundException => (StatusCodes.Status404NotFound, "Not found", exception.Message),
            ConflictException => (StatusCodes.Status409Conflict, "Conflict", exception.Message),
            FinanceRequestException => (StatusCodes.Status502BadGateway, "Finance is not available", "The finance service did not give an answer. Try again later."),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred", null)
        };

        if (status == StatusCodes.Status502BadGateway)
        {
            logger.LogWarning(exception, "Finance call failed for {Method} {Path}", context.Request.Method, context.Request.Path);
        }
        else if (status == StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
        }

        context.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = new ProblemDetails { Status = status, Title = title, Detail = detail }
        });
    }
}
