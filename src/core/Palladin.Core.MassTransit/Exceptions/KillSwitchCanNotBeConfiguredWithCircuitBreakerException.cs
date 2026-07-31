namespace Palladin.Core.MassTransit.Exceptions;

public class KillSwitchCanNotBeConfiguredWithCircuitBreakerException : Exception
{
    public KillSwitchCanNotBeConfiguredWithCircuitBreakerException() : base(
        "Circuit breaker and kill switch cannot be used at the same time. Please choose one of them."
    )
    {
    }
}
