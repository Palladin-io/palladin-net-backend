
namespace Palladin.Tests.Unit.Architecture;

public sealed class MigrationHistoryArchitectureTests
{
    private static readonly string[] ModuleNames =
    [
        "Agents",
        "Audit",
        "Identity",
        "Notification",
        "Search",
        "Vault",
    ];

    [Fact]
    public void EveryActiveContext_ShouldHaveExactlyOneInitialMigration()
    {
        var root = FindRepositoryRoot();

        foreach (var moduleName in ModuleNames)
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
        var violations = ModuleNames
            .SelectMany(moduleName => Directory.EnumerateFiles(
                Path.Combine(root, "src", "modules", $"Palladin.Module.{moduleName}", "Infrastructure", "Persistence"),
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
        Path.Combine(root, "src", "modules", $"Palladin.Module.{moduleName}", "Infrastructure", "Persistence", "Migrations");

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
