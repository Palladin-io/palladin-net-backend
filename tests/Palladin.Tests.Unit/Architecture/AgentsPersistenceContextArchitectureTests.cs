using System.Reflection;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.Persistence;

namespace Palladin.Tests.Unit.Architecture;

public sealed class AgentsPersistenceContextArchitectureTests
{
    [Fact]
    public void When_AgentFeatureUsesPersistence_Then_ItDoesNotInjectBothDomainContexts()
    {
        // Given
        var features = typeof(Agent).Assembly.GetTypes()
            .Where(type => type.Namespace == "Palladin.Module.Agents.Features");

        // When
        var violations = features.Where(type =>
            {
                var dependencies = new HashSet<Type>();
                CollectDependencies(type, dependencies);
                return dependencies.Contains(typeof(AgentsDomainReadContext))
                    && dependencies.Contains(typeof(AgentsDomainWriteContext));
            })
            .Select(type => type.Name).Order().ToArray();

        // Then
        violations.ShouldBeEmpty("a mutation uses one DomainWriteContext for its reads and writes");
    }

    private static void CollectDependencies(Type type, HashSet<Type> dependencies)
    {
        if (!dependencies.Add(type) || type.Namespace != "Palladin.Module.Agents.Features")
        {
            return;
        }

        foreach (var parameter in type
                     .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                     .SelectMany(constructor => constructor.GetParameters()))
        {
            CollectDependencies(parameter.ParameterType, dependencies);
        }
    }
}
