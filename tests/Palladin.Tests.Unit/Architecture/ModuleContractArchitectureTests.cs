
namespace Palladin.Tests.Unit.Architecture;

public sealed class ModuleContractArchitectureTests
{
    private static readonly (string Name, string GroupPath)[] ModuleGroups =
    [
        ("Agents", "Agents"),
        ("Audit", Path.Combine("OpenHost", "Audit")),
        ("Identity", "Identity"),
        ("Notification", Path.Combine("OpenHost", "Notification")),
        ("PublicAssetCatalog", Path.Combine("OpenHost", "PublicAssetCatalog")),
        ("Search", Path.Combine("OpenHost", "Search")),
        ("Vault", "Vault"),
    ];

    [Fact]
    public void ModuleGroups_ShouldKeepImplementationAndContractsAsSiblingProjects()
    {
        var root = FindRepositoryRoot();

        foreach (var (name, groupPath) in ModuleGroups)
        {
            var groupDirectory = Path.Combine(root, "src", "modules", groupPath);
            var implementationDirectory = Path.Combine(groupDirectory, $"Palladin.Module.{name}");
            var contractsDirectory = Path.Combine(groupDirectory, $"Palladin.Module.{name}.Contracts");

            File.Exists(Path.Combine(groupDirectory, "README.md")).ShouldBeTrue();
            File.Exists(Path.Combine(implementationDirectory, $"Palladin.Module.{name}.csproj")).ShouldBeTrue();
            File.Exists(Path.Combine(contractsDirectory, $"Palladin.Module.{name}.Contracts.csproj")).ShouldBeTrue();
            Directory.Exists(Path.Combine(implementationDirectory, "Contracts")).ShouldBeFalse();
        }
    }

    [Fact]
    public void IntegrationMessages_ShouldLiveInTheirSemanticContractDirectory()
    {
        var root = FindRepositoryRoot();
        var moduleFiles = Directory.EnumerateFiles(
                Path.Combine(root, "src", "modules"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        var violations = moduleFiles
            .Select(file => new
            {
                File = file,
                RelativePath = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'),
                Source = File.ReadAllText(file),
            })
            .SelectMany(item =>
            {
                var errors = new List<string>();
                if (item.Source.Contains("IIntegrationEvent", StringComparison.Ordinal)
                    && !item.RelativePath.Contains(".Contracts/Events/", StringComparison.Ordinal))
                {
                    errors.Add($"{item.RelativePath}: IIntegrationEvent outside Contracts/Events");
                }

                if (item.Source.Contains("IIntegrationCommand", StringComparison.Ordinal)
                    && !item.RelativePath.Contains(".Contracts/Commands/", StringComparison.Ordinal))
                {
                    errors.Add($"{item.RelativePath}: IIntegrationCommand outside Contracts/Commands");
                }

                if (item.RelativePath.Contains(".Contracts/Events/", StringComparison.Ordinal)
                    && item.Source.Contains("IIntegrationCommand", StringComparison.Ordinal))
                {
                    errors.Add($"{item.RelativePath}: command marker in Contracts/Events");
                }

                if (item.RelativePath.Contains(".Contracts/Commands/", StringComparison.Ordinal)
                    && item.Source.Contains("IIntegrationEvent", StringComparison.Ordinal))
                {
                    errors.Add($"{item.RelativePath}: event marker in Contracts/Commands");
                }

                return errors;
            })
            .ToList();

        violations.ShouldBeEmpty(
            "integration commands and events have different semantics and must remain explicit public contracts");
    }

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
