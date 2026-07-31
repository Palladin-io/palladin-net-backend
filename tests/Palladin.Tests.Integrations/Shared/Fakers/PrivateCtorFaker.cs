using AutoBogus;
using Bogus;

namespace Palladin.Tests.Integrations.Shared.Fakers;

public class PrivateCtorAutoFaker<T> : AutoFaker<T> where T : class
{
    public PrivateCtorAutoFaker()
    {
        ResolvePrivateConstructor(this);
    }

    private static void ResolvePrivateConstructor(Faker<T> f)
    {
        f.CustomInstantiator(_ => (Activator.CreateInstance(typeof(T), nonPublic: true) as T)!);
    }
}

public class PrivateCtorFaker<T> : Faker<T> where T : class
{
    public PrivateCtorFaker() : base(binder: new IncludePrivateBinder())
    {
        ResolvePrivateConstructor(this);
    }

    private static void ResolvePrivateConstructor(Faker<T> f)
    {
        f.CustomInstantiator(_ => (Activator.CreateInstance(typeof(T), nonPublic: true) as T)!);
    }
}
