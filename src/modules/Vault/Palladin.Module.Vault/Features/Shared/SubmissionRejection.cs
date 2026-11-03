using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

// Agent Submission endpoints answer every business rejection with the contract's value-free
// `{ "code": "<kebab>" }` body so CLI, MCP and web map outcomes without parsing messages.
internal sealed class SubmissionRejection(int statusCode, string code) : Exception(code)
{
    internal int StatusCode { get; } = statusCode;
    internal string Code { get; } = code;

    internal static SubmissionRejection From(Exception exception) => exception switch
    {
        SubmissionRejection rejection => rejection,
        AgentSubmissionInputException input => new SubmissionRejection(StatusCodes.Status400BadRequest, input.Code),
        AgentSubmissionConflictException conflict => new SubmissionRejection(StatusCodes.Status409Conflict, conflict.Code),
        _ => throw new InvalidOperationException("Unsupported submission rejection.", exception),
    };

    internal static bool Handles(Exception exception) =>
        exception is SubmissionRejection or AgentSubmissionInputException or AgentSubmissionConflictException;

    internal Task SendAsync(HttpContext context, CancellationToken ct) =>
        context.Response.SendAsync(new SubmissionErrorResponse(Code), StatusCode, cancellation: ct);
}
