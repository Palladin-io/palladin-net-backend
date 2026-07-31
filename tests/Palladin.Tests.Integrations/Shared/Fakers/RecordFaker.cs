using System.Runtime.Serialization;
using AutoBogus;
using Bogus;

namespace Palladin.Tests.Integrations.Shared.Fakers;

public class RecordFaker<T> : Faker<T> where T : class
{
    public RecordFaker()
    {
#pragma warning disable SYSLIB0050
        CustomInstantiator(_ => (T)FormatterServices.GetUninitializedObject(typeof(T)));
#pragma warning restore SYSLIB0050
    }
}

public class RecordAutoFaker<T> : AutoFaker<T> where T : class
{
    public RecordAutoFaker()
    {
#pragma warning disable SYSLIB0050
        CustomInstantiator(_ => (T)FormatterServices.GetUninitializedObject(typeof(T)));
#pragma warning restore SYSLIB0050
    }
}
