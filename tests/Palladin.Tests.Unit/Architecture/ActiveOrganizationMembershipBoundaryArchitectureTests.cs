using System.Text.RegularExpressions;

namespace Palladin.Tests.Unit.Architecture;

public sealed partial class ActiveOrganizationMembershipBoundaryArchitectureTests
{
    private static readonly string[] ReviewedExemptEndpoints =
    [
        "AcceptOrganizationInvitationEndpoint",
        "ChangePasswordEndpoint",
        "ConfirmTotpEndpoint",
        "DisableTotpEndpoint",
        "EnrollTotpEndpoint",
        "GetMemberDeltaEndpoint",
        "GetMemberSnapshotEndpoint",
        "GlobalSearchEndpoint",
        "LogoutEndpoint",
        "MarkAllNotificationsReadEndpoint",
        "MarkNotificationReadEndpoint",
        "RecoverAccountEndpoint",
        "RegisterPushTokenEndpoint",
        "RemovePushTokenEndpoint",
        "ResendVerificationEmailEndpoint",
        "SetupAccountEndpoint",
        "SwitchOrganizationEndpoint",
        "UpdateNotificationPreferencesEndpoint",
    ];

    [Fact]
    public void EveryEndpoint_ShouldReceiveTheUnsafeUserJwtBoundaryByDefault()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "src", "Palladin.Api", "Program.cs"));
        var preProcessor = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Palladin.Api",
            "Framework",
            "RequireActiveOrganizationMembershipPreProcessor.cs"));

        program.ShouldContain(
            "endpointDefinition.PreProcessor<RequireActiveOrganizationMembershipPreProcessor>(Order.Before)");
        program.ShouldNotContain("DomainWriteEndpointClassifier");
        preProcessor.ShouldContain("HttpMethods.IsGet(method)");
        preProcessor.ShouldContain("validator.IsActiveAsync(");
    }

    [Fact]
    public void NonActiveMembershipExemptions_ShouldMatchTheReviewedAllowListExactly()
    {
        var root = FindRepositoryRoot();
        var exemptEndpoints = Directory.EnumerateFiles(
                Path.Combine(root, "src"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .SelectMany(path => ExemptEndpointPattern().Matches(File.ReadAllText(path))
                .Select(match => match.Groups["name"].Value))
            .Order(StringComparer.Ordinal)
            .ToArray();

        exemptEndpoints.ShouldBe(ReviewedExemptEndpoints.Order(StringComparer.Ordinal).ToArray());
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
        "\\[AllowNonActiveOrganizationMembership\\]\\s*internal\\s+sealed\\s+class\\s+(?<name>\\w+Endpoint)",
        RegexOptions.CultureInvariant)]
    private static partial Regex ExemptEndpointPattern();
}
