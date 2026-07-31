namespace Palladin.Core.Events;

public interface IEvent;

public interface IIntegrationMessage : IEvent;

public interface IIntegrationEvent : IIntegrationMessage;

public interface IIntegrationCommand : IIntegrationMessage;
