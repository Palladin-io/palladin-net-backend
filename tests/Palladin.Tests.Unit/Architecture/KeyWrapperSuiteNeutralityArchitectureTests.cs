using System.Text.RegularExpressions;

namespace Palladin.Tests.Unit.Architecture;

// CVT-721 agility gate: retiring a suite must touch only the registry, the crypto implementation and its vectors.
public sealed partial class KeyWrapperSuiteNeutralityArchitectureTests
{
    private static readonly string[] CryptoInfrastructure =
    [
        "/src/core/Palladin.Core.Cryptography/",
        "/Infrastructure/Crypto/",
        "/Infrastructure/Recovery/",
        "/Infrastructure/Persistence/Migrations/",
    ];

    [Fact]
    public void ProductCode_ShouldNotNameAConcreteKeyWrapperSuiteOutsideCryptoInfrastructure()
    {
        // Given
        var root = FindRepositoryRoot();

        // When
        var violations = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Select(path => path.Replace(Path.DirectorySeparatorChar, '/'))
            .Where(path => !path.Contains("/obj/", StringComparison.Ordinal) && !path.Contains("/bin/", StringComparison.Ordinal))
            .Where(path => !CryptoInfrastructure.Any(allowed => path.Contains(allowed, StringComparison.Ordinal)))
            .SelectMany(path => ConcreteSuitePattern().Matches(File.ReadAllText(path))
                .Select(match => $"{Path.GetRelativePath(root, path)}: {match.Value}"))
            .ToList();

        // Then
        violations.ShouldBeEmpty();
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

    [GeneratedRegex(
        "palladin-(x25519-sealed-box|xwing)-v1|X25519SealedBox|XWing\\w*|crypto_box_seal",
        RegexOptions.CultureInvariant)]
    private static partial Regex ConcreteSuitePattern();
}
