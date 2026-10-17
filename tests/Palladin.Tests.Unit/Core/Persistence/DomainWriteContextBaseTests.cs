using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NSubstitute;
using Palladin.Core.Events;
using Palladin.Core.Persistence;

namespace Palladin.Tests.Unit.Core.Persistence;

public sealed class DomainWriteContextBaseTests
{
    [Fact]
    public async Task When_PreparingDurableEvents_Then_PreparationRunsOnceBeforeEachSave()
    {
        // Given
        var calls = new List<string>();
        await using var dbContext = new TestDbContext(calls);
        var context = new PreparingDomainWriteContext(dbContext, calls);
        var transaction = Substitute.For<IDbContextTransaction>();

        // When
        await context.CommitAsync(TestContext.Current.CancellationToken);
        await context.FlushAsync(TestContext.Current.CancellationToken);
        await context.CommitAsync(transaction, TestContext.Current.CancellationToken);

        // Then
        calls.ShouldBe(["prepare", "save", "prepare", "save", "prepare", "save"]);
    }

    [Fact]
    public async Task When_CommittingExplicitTransaction_Then_PublishesEventsAfterTransactionCommit()
    {
        // Given
        var calls = new List<string>();
        await using var dbContext = new TestDbContext(calls);
        var publisher = Substitute.For<IEventPublisher>();
        publisher.PublishAsync(Arg.Any<IEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Add("publish");
                return Task.CompletedTask;
            });
        var transaction = Substitute.For<IDbContextTransaction>();
        transaction.CommitAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Add("commit");
                return Task.CompletedTask;
            });
        var context = new TestDomainWriteContext(dbContext, [publisher]);
        context.Add(TestEntity.Create());

        // When
        await context.CommitAsync(transaction, TestContext.Current.CancellationToken);

        // Then
        calls.ShouldBe(["save", "commit", "publish"]);
    }

    [Fact]
    public async Task When_FlushedPagesAreCleared_Then_PublishesBufferedEventsAfterTransactionCommit()
    {
        // Given
        var calls = new List<string>();
        await using var dbContext = new TestDbContext(calls);
        var publisher = Substitute.For<IEventPublisher>();
        publisher.PublishAsync(Arg.Any<IEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Add("publish");
                return Task.CompletedTask;
            });
        var transaction = Substitute.For<IDbContextTransaction>();
        transaction.CommitAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Add("commit");
                return Task.CompletedTask;
            });
        var context = new TestDomainWriteContext(dbContext, [publisher]);
        context.Add(TestEntity.Create());

        // When
        await context.FlushAsync(TestContext.Current.CancellationToken);
        context.Clear();
        await context.CommitAsync(transaction, TestContext.Current.CancellationToken);

        // Then
        calls.ShouldBe(["save", "save", "commit", "publish"]);
    }

    private sealed record TestEvent : IEvent;

    private sealed class TestEntity : EventEntityBase
    {
        public int Id { get; private init; }

        internal static TestEntity Create()
        {
            var entity = new TestEntity { Id = 1 };
            entity.AddEvent(new TestEvent());
            return entity;
        }
    }

    private sealed class TestDbContext(List<string> calls) : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            optionsBuilder.UseNpgsql("Host=localhost;Database=unused");

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<TestEntity>().HasKey(x => x.Id);

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            calls.Add("save");
            return Task.FromResult(1);
        }
    }

    private sealed class TestDomainWriteContext(
        TestDbContext writeContext,
        IEnumerable<IEventPublisher> eventPublishers)
        : DomainWriteContextBase(writeContext, eventPublishers);

    private sealed class PreparingDomainWriteContext(TestDbContext writeContext, List<string> calls)
        : DomainWriteContextBase(writeContext, [])
    {
        protected override Task PrepareEventsAsync(CancellationToken cancellationToken)
        {
            calls.Add("prepare");
            return Task.CompletedTask;
        }
    }
}
