using Palladin.Core.MassTransit.Exceptions;

namespace Palladin.Core.MassTransit.Validation;

public static class MassTransitOptionsValidation
{
    public static void Validate(this MassTransitOptions options)
    {
        if (options.KillSwitch.Disabled == false && options.CircuitBreaker.Disabled == false)
        {
            throw new KillSwitchCanNotBeConfiguredWithCircuitBreakerException();
        }
    }
}
