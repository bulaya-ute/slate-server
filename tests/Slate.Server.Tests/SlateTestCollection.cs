namespace Slate.Server.Tests;

/// <summary>
/// Shared xUnit collection: every test class using the real app + Testcontainers Postgres
/// should declare [Collection(SlateTestCollection.Name)] and take a TestApp constructor
/// parameter, so the whole test session boots exactly one Postgres container.
/// </summary>
[CollectionDefinition(Name)]
public class SlateTestCollection : ICollectionFixture<TestApp>
{
    public const string Name = "Slate server collection";
}
