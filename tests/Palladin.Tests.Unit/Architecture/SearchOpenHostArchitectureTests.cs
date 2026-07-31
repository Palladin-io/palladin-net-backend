using Palladin.Core.Events;
using Palladin.Module.Search.Contracts.Commands;
using Palladin.Module.Search.Contracts.ValueObjects;

namespace Palladin.Tests.Unit.Architecture;

public sealed class SearchOpenHostArchitectureTests
{
    [Fact]
    public void When_SearchOpenHostMessagesAreNamedCommands_Then_TheyUseOnlyTheCommandMarker()
    {
        // Given
        var commands = typeof(IndexSearchItemCommand).Assembly.GetTypes()
            .Where(type => type.Name.EndsWith("Command", StringComparison.Ordinal))
            .ToList();

        // When
        var eventCommands = commands
            .Where(type => typeof(IIntegrationEvent).IsAssignableFrom(type))
            .ToList();

        // Then
        commands.ShouldNotBeEmpty();
        commands.ShouldAllBe(type => typeof(IIntegrationCommand).IsAssignableFrom(type));
        eventCommands.ShouldBeEmpty();
    }
}
