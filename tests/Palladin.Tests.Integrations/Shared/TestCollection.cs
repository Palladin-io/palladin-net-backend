namespace Palladin.Tests.Integrations.Shared;

public abstract class TestCollection<TFixture> : ICollectionFixture<TFixture> where TFixture : class;
