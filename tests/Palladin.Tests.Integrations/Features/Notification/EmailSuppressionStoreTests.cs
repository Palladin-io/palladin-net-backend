using Palladin.Module.Notification.Domain;
using Palladin.Module.Notification.Infrastructure.Email.Suppression;
using Palladin.Module.Notification.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Notification;

[Collection<ApiFactoryCollection>]
public sealed class EmailSuppressionStoreTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_SameAddressSuppressedTwice_Then_SingleRowRegardlessOfCasing()
    {
        // Given
        var address = $"Bounce-{Guid.NewGuid():N}@Example.com";
        var normalized = address.ToLowerInvariant();

        // When
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IEmailSuppressionStore>();
            await store.SuppressAsync(address, SuppressionReason.HardBounce, TestContext.Current.CancellationToken);
            await store.SuppressAsync(address.ToUpperInvariant(), SuppressionReason.Complaint, TestContext.Current.CancellationToken);
        }

        // Then
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = scope.ServiceProvider.GetRequiredService<NotificationDbWriteContext>();
            var rows = await writeContext.SuppressedEmails
                .Where(x => x.Address == normalized)
                .ToListAsync(TestContext.Current.CancellationToken);

            rows.ShouldHaveSingleItem();
            rows[0].Reason.ShouldBe(SuppressionReason.HardBounce);

            var store = scope.ServiceProvider.GetRequiredService<IEmailSuppressionStore>();
            (await store.IsSuppressedAsync(address, TestContext.Current.CancellationToken)).ShouldBeTrue();
        }
    }
}
