
namespace Palladin.Tests.Unit.Architecture;

public sealed class MigrationHistoryArchitectureTests
{
    private static readonly Dictionary<string, string> ModuleGroupPaths = new()
    {
        ["Agents"] = "Agents",
        ["Audit"] = Path.Combine("OpenHost", "Audit"),
        ["Identity"] = "Identity",
        ["Notification"] = Path.Combine("OpenHost", "Notification"),
        ["Search"] = Path.Combine("OpenHost", "Search"),
        ["Vault"] = "Vault",
    };

    [Fact]
    public void EveryActiveContext_ShouldHaveExactlyOneInitialMigration()
    {
        var root = FindRepositoryRoot();

        foreach (var moduleName in ModuleGroupPaths.Keys)
        {
            var migrations = Directory.EnumerateFiles(GetMigrationsDirectory(root, moduleName), "*.cs")
                .Where(path => !path.EndsWith(".Designer.cs", StringComparison.Ordinal)
                               && !path.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal))
                .Select(Path.GetFileName)
                .ToList();

            migrations.ShouldHaveSingleItem($"{moduleName} has one pre-production migration history")
                .ShouldEndWith("_Initial.cs");
        }
    }

    [Fact]
    public void PersistenceModelAndMigrations_ShouldNeverDeclareDatabaseValidationOrTriggers()
    {
        var root = FindRepositoryRoot();
        var violations = ModuleGroupPaths.Keys
            .SelectMany(moduleName => Directory.EnumerateFiles(
                GetPersistenceDirectory(root, moduleName),
                "*.cs",
                SearchOption.AllDirectories))
            .SelectMany(path => new[]
                {
                    "HasCheckConstraint",
                    "AddCheckConstraint",
                    "CHECK (",
                    "CREATE TRIGGER",
                    "CREATE OR REPLACE FUNCTION",
                }
                .Where(forbidden => File.ReadAllText(path).Contains(
                    forbidden,
                    forbidden.StartsWith("CREATE", StringComparison.Ordinal)
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
                .Select(forbidden => $"{Path.GetRelativePath(root, path)}: {forbidden}"))
            .ToList();

        violations.ShouldBeEmpty(
            "validation and behavior belong in domain methods and application services, never PostgreSQL triggers or functions");
    }

    private static string GetMigrationsDirectory(string root, string moduleName) =>
        Path.Combine(GetPersistenceDirectory(root, moduleName), "Migrations");

    private static string GetPersistenceDirectory(string root, string moduleName) =>
        Path.Combine(
            root,
            "src",
            "modules",
            ModuleGroupPaths[moduleName],
            $"Palladin.Module.{moduleName}",
            "Infrastructure",
            "Persistence");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Palladin.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException("Could not locate the backend repository root.");
    }
}
