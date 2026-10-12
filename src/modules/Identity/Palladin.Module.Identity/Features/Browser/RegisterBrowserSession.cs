using Palladin.Core.Guid;
using Palladin.Core.Persistence;
using Palladin.Core.Security;
using Palladin.Core.Transport;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Infrastructure;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Login;
using Palladin.Module.Identity.Infrastructure.PasswordAuth;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Shared;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using NodaTime;
using System.Text.Json.Serialization;
using Palladin.Core.Json;

using Palladin.Module.Identity.Infrastructure.BrowserSessions;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
internal sealed class RegisterBrowserSessionEndpoint(RegisterOperation operation, BrowserSessionResponseWriter writer)
    : IdentityOperationEndpoint<RegisterRequest, object>
{
    public override void Configure()
    {
        Post("api/browser/auth/register");
        // Anonymous by design: this creates the account. Zero-knowledge material (salt, wrapped keys)
        // and the re-hashed authHash are stored; the password and master key never reach the server.
        AllowAnonymous();
        Summary(summary =>
        {
            summary.Summary = "Register with email + password";
            summary.Description = "Creates an organization and admin user with the client-generated AccountId "
                + "bound into the password-only versioned Identity KDF. "
                + "Stores the client authHash re-hashed with Argon2id, plus the zero-knowledge key material. "
                + "Issues a session and sends an email-verification link (emailVerified starts false).";
        });
        Validator<RegisterValidator>();
        Tags("Identity/Auth");
    }

    public override async Task HandleAsync(RegisterRequest req, CancellationToken ct)
    {
        var result = await operation.ExecuteAsync(req, HttpContext, ct);
        await SendResultAsync(await writer.MapAsync(result, HttpContext, ct), ct);
    }
}
