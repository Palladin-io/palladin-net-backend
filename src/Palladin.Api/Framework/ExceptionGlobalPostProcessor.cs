using Palladin.Core.Types.Exceptions;
using FastEndpoints;
using FluentValidation.Results;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Api.Framework;

[UsedImplicitly]
public sealed class ExceptionGlobalPostProcessor : IGlobalPostProcessor
{
    public async Task PostProcessAsync(IPostProcessorContext context, CancellationToken ct)
    {
        if (context.ExceptionDispatchInfo?.SourceException is DomainException domainException)
        {
            context.MarkExceptionAsHandled();

            await context.HttpContext.Response.SendErrorsAsync(
                [
                    ..context.ValidationFailures,
                    new ValidationFailure("generalErrors", domainException.Message),
                ],
                cancellation: ct);
        }
        else if (context.ExceptionDispatchInfo?.SourceException is EntityNotFoundException entityNotFoundException)
        {
            context.MarkExceptionAsHandled();

            await context.HttpContext.Response.SendErrorsAsync(
                [
                    ..context.ValidationFailures,
                    new ValidationFailure("generalErrors", entityNotFoundException.Message),
                ],
                404,
                cancellation: ct);
        }
        else if (context.ExceptionDispatchInfo?.SourceException is ConflictException conflictException)
        {
            context.MarkExceptionAsHandled();

            await context.HttpContext.Response.SendErrorsAsync(
                [
                    ..context.ValidationFailures,
                    new ValidationFailure("generalErrors", conflictException.Message),
                ],
                StatusCodes.Status409Conflict,
                cancellation: ct);
        }
        else if (context.ExceptionDispatchInfo?.SourceException is DbUpdateConcurrencyException)
        {
            context.MarkExceptionAsHandled();

            await context.HttpContext.Response.SendErrorsAsync(
                [
                    ..context.ValidationFailures,
                    new ValidationFailure("generalErrors", "The resource was modified by another request. Please retry."),
                ],
                StatusCodes.Status409Conflict,
                cancellation: ct);
        }
    }
}
