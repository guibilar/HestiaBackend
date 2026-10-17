using Hestia.Platform.Exceptions;
using Hestia.Platform.Finance;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hestia.Platform.Errors;

/// <summary>
/// Turns exceptions into RFC 9457 problem details. Platform exceptions become 400/404/409 with
/// their message; a <see cref="DbUpdateConcurrencyException"/> is a 409 too. A request that cannot be read (<see cref="BadHttpRequestException"/>, for example a bad enum value or a
/// body over the size limit) gets its own status code (400, 413), as in Production. A failed finance call (<see cref="FinanceRequestException"/>) is logged and becomes 502.
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

        if (exception is BadHttpRequestException badRequest)
        {
            // Minimal APIs throw this in Development (RouteHandlerOptions.ThrowOnBadRequest), and Kestrel throws it for a
            // body over the limit. In Production, minimal APIs set the status code without a body, and the status code
            // pages write the problem details. This gives the same answer: the status code and its reason phrase.
            var badStatus = badRequest.StatusCode is >= 400 and < 500 ? badRequest.StatusCode : StatusCodes.Status400BadRequest;
            logger.LogDebug(exception, "Bad request for {Method} {Path}", context.Request.Method, context.Request.Path);
            context.Response.StatusCode = badStatus;
            return await problemDetails.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                Exception = exception,
                ProblemDetails = new ProblemDetails { Status = badStatus, Title = ReasonPhrases.GetReasonPhrase(badStatus) }
            });
        }

        var (status, title, detail) = exception switch
        {
            NotFoundException => (StatusCodes.Status404NotFound, "Not found", exception.Message),
            ConflictException => (StatusCodes.Status409Conflict, "Conflict", exception.Message),
            // A row with a concurrency token (the xmin of a task) that another request changed since this one read it.
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "Conflict", "It changed at the same time. Read it again, then try again."),
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
