using MassTransit;
using Microsoft.Extensions.Logging;
using NodaTime;
using NSubstitute;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Triggers;
using Shouldly;

namespace Palladin.Tests.Unit.Modules.Identity;

public sealed class LoginAttemptFailedTriggerTests
{
    [Fact]
    public async Task When_AttemptFails_Then_StructuredSecurityWarningContainsNoCredential()
    {
        var logger = new RecordingLogger<OnLoginAttemptFailedSecurityLog>();
        var @event = new LoginAttemptFailedEvent(
            Guid.NewGuid(),
            new string('c', 64),
            "192.0.2.10",
            Guid.NewGuid(),
            Guid.NewGuid(),
            LoginAttemptFactor.Totp,
            Instant.FromUtc(2026, 8, 22, 12, 0));
        var context = Substitute.For<ConsumeContext<LoginAttemptFailedEvent>>();
        context.Message.Returns(@event);

        await new OnLoginAttemptFailedSecurityLog(logger).Consume(context);

        var warning = logger.Messages.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain(@event.AttemptId.ToString());
        warning.Message.ShouldContain(@event.EmailHash);
        warning.Message.ShouldContain(@event.IpAddress);
        warning.Message.ShouldContain(LoginAttemptFactor.Totp);
        warning.Message.ShouldNotContain("member@example.com");
        warning.Message.ShouldNotContain("123456");
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        internal List<(LogLevel Level, string Message)> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add((logLevel, formatter(state, exception)));
    }
}
