using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Agents.Contracts.Events;
using Palladin.Module.Agents.Triggers;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Triggers;
using Palladin.Module.Search.Contracts.Commands;
using Palladin.Module.Search.Contracts.ValueObjects;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Mocks;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Search;

[Collection<ApiFactoryCollection>]
public sealed class SearchTriggerTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task AgentUpsert_PublishesOnlyAdministrativeFields()
    {
        var (endpoint, get) = Capture<IndexSearchItemCommand>();
        var agentId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        await new OnAgentUpsertedSearch(endpoint).Consume(apiFactory.MockConsumeContext(
            new AgentUpsertedEvent(agentId, organizationId, AgentStatus.Active, "pk", 1,
                "signing-pk", "Bot", "cli", null, null, 1, Now(), Now())));

        var command = get().ShouldNotBeNull();
        command.OrganizationId.ShouldBe(organizationId);
        command.ItemId.ShouldBe(agentId);
        command.Type.ShouldBe(SearchItemTypes.Agent);
        command.Name.ShouldBe("Bot");
        command.SearchTerms.ShouldBeEmpty();
    }

    [Fact]
    public async Task MemberUpsert_PublishesDisplayNameAndEmailSearchTerm()
    {
        var (endpoint, get) = Capture<IndexSearchItemCommand>();
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDomainReadContext>();
        await new OnUserUpsertedSearch(endpoint, readContext).Consume(apiFactory.MockConsumeContext(
            new UserUpsertedEvent(user.Id, organization.Id, "Ada", "ada@example.test", Permission.None, Now())));

        var command = get().ShouldNotBeNull();
        command.OrganizationId.ShouldBe(organization.Id);
        command.ItemId.ShouldBe(user.Id);
        command.Type.ShouldBe(SearchItemTypes.Member);
        command.Name.ShouldBe("Ada");
        command.SearchTerms.ShouldBe(["ada@example.test"]);
    }

    [Fact]
    public async Task MemberJoin_IndexesTheJoinedOrganization()
    {
        var (endpoint, get) = Capture<IndexSearchItemCommand>();
        var organizationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await new OnOrganizationMemberJoinedSearch(endpoint).Consume(apiFactory.MockConsumeContext(
            new OrganizationMemberJoinedEvent(
                organizationId, userId, "Ada", "ada@example.test", "Member", Now())));

        var command = get().ShouldNotBeNull();
        command.OrganizationId.ShouldBe(organizationId);
        command.ItemId.ShouldBe(userId);
        command.Type.ShouldBe(SearchItemTypes.Member);
    }

    [Fact]
    public async Task Deletes_AreTenantScoped()
    {
        var agentEndpoint = Capture<RemoveSearchItemCommand>();
        var agentOrganizationId = Guid.NewGuid();
        await new OnAgentDeletedSearch(agentEndpoint.Endpoint).Consume(apiFactory.MockConsumeContext(
            new AgentDeletedEvent(Guid.NewGuid(), agentOrganizationId, Guid.NewGuid(), "Operator", "Bot", Now())));
        agentEndpoint.Get().ShouldNotBeNull().OrganizationId.ShouldBe(agentOrganizationId);

        var memberEndpoint = Capture<RemoveSearchItemCommand>();
        var memberOrganizationId = Guid.NewGuid();
        await new OnOrganizationMemberRemovedSearch(memberEndpoint.Endpoint).Consume(apiFactory.MockConsumeContext(
            new OrganizationMemberRemovedEvent(memberOrganizationId, Guid.NewGuid(), "Member",
                Guid.NewGuid(), "Operator", Now())));
        memberEndpoint.Get().ShouldNotBeNull().OrganizationId.ShouldBe(memberOrganizationId);
    }

    private static (IPublishEndpoint Endpoint, Func<T?> Get) Capture<T>() where T : class
    {
        T? captured = null;
        var endpoint = Substitute.For<IPublishEndpoint>();
        endpoint.When(p => p.Publish(Arg.Any<T>(), Arg.Any<CancellationToken>()))
            .Do(call => captured = call.Arg<T>());
        return (endpoint, () => captured);
    }

    private Instant Now() => apiFactory.FakeClock.GetCurrentInstant();
}
