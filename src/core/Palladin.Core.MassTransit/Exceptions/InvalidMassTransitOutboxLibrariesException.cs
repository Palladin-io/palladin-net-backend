namespace Palladin.Core.MassTransit.Exceptions;

public class InvalidMassTransitOutboxLibrariesException : Exception
{
    public InvalidMassTransitOutboxLibrariesException() : base(
        $"Invalid count of MassTransit outbox providers. Should be only one."
    )
    {
    }
}
