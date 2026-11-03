using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Shared;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault.AgentSubmissions;

// Binds the backend's closed wire shapes and registrations to the vendored palladin-protocol contract.json, so a
// contract change that the backend has not adopted fails here instead of in a cross-repository E2E run.
public sealed class AgentSubmissionContractDriftTests
{
    private static readonly JsonNode Contract = JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "AgentSubmission", "contract-v1.json")))!;

    [Fact]
    public void When_ContractTransportFieldsAreCompared_Then_TheBackendShapeIsExactlyTheContractShape()
    {
        // Given
        var contractFields = Contract["submission"]!["transportFields"]!.AsArray().Select(x => x!.GetValue<string>());
        var manifestFields = Contract["approverKeySet"]!["manifest"]!["requiredFields"]!.AsArray().Select(x => x!.GetValue<string>())
            .Concat(Contract["approverKeySet"]!["manifest"]!["conditionalFields"]!.AsObject().Select(x => x.Key));

        // When
        var backendFields = WireNames(typeof(AgentEntrySubmissionContract));
        var backendManifestFields = WireNames(typeof(OrganizationApproverManifestContract));

        // Then
        backendFields.ShouldBe(contractFields.Order(StringComparer.Ordinal));
        backendManifestFields.ShouldBe(manifestFields.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void When_ContractRegistrationsAreCompared_Then_BackendIdentifiersMatch()
    {
        // Given
        var registrations = Contract["registrations"]!;
        var purposes = registrations["x25519WrapperPurposes"]!.AsArray().ToDictionary(
            x => x!["name"]!.GetValue<string>(), x => x!["id"]!.GetValue<int>());
        var keyKinds = registrations["keyKinds"]!.AsArray().ToDictionary(
            x => x!["name"]!.GetValue<string>(), x => x!["id"]!.GetValue<int>());

        // When / Then
        purposes["submission-dek"].ShouldBe((int)X25519WrapperPurpose.SubmissionDek);
        purposes["approver-message-private-key"].ShouldBe(ApproverKeyWrap.MessagePrivateKeyPurpose);
        purposes["approver-signing-private-key"].ShouldBe(ApproverKeyWrap.SigningPrivateKeyPurpose);
        keyKinds["organization-approver-x25519"].ShouldBe((int)VaultKeyKind.OrganizationApproverX25519);
        keyKinds["organization-approver-ed25519"].ShouldBe((int)VaultKeyKind.OrganizationApproverEd25519);
        Contract["approverKeySet"]!["privateKeyDistribution"]!["scopeBitmap"]!.GetValue<int>()
            .ShouldBe(AgentSubmissionCrypto.ApproverWrapScopeBitmap);
        Contract["approverKeySet"]!["manifest"]!["schema"]!.GetValue<string>().ShouldBe(AgentSubmissionCrypto.ManifestSchema);
        Contract["submission"]!["schema"]!.GetValue<string>().ShouldBe(AgentSubmissionCrypto.SubmissionSchema);
        Contract["submission"]!["dekWrapping"]!["decodedSealedPackageBytes"]!.GetValue<int>()
            .ShouldBe(X25519SealedBoxContract.EncodedPackageBytes);
        Contract["protocolVersion"]!.GetValue<int>().ShouldBe(VaultProtocol.CurrentVersion);
        Contract["contractVersion"]!.GetValue<int>().ShouldBe(AgentSubmissionLimits.ContractVersion);
    }

    [Fact]
    public void When_ContractEnumsAndLimitsAreCompared_Then_BackendValuesMatch()
    {
        // Given
        var enums = Contract["enums"]!;
        var limits = Contract["limits"]!;

        // When
        var kinds = enums["SubmissionKind"]!["values"]!.AsArray().ToDictionary(x => x!["name"]!.GetValue<string>(), x => x!["id"]!.GetValue<int>());
        var states = enums["SubmissionState"]!["values"]!.AsArray().ToDictionary(x => x!["name"]!.GetValue<string>(), x => x!["id"]!.GetValue<int>());
        var entryTypes = enums["SubmissionEntryType"]!["allowed"]!.AsArray().ToDictionary(x => x!["name"]!.GetValue<string>(), x => x!["id"]!.GetValue<int>());

        // Then
        kinds.ShouldBe(new Dictionary<string, int> { ["new"] = (int)AgentSubmissionKind.New, ["update"] = (int)AgentSubmissionKind.Update });
        states.ShouldBe(new Dictionary<string, int>
        {
            ["pending"] = (int)AgentSubmissionState.Pending,
            ["approved"] = (int)AgentSubmissionState.Approved,
            ["trashed"] = (int)AgentSubmissionState.Trashed,
            ["purged"] = (int)AgentSubmissionState.Purged,
        });
        entryTypes.ShouldBe(new Dictionary<string, int>
        {
            ["key"] = (int)AgentSubmissionEntryType.Key,
            ["credential"] = (int)AgentSubmissionEntryType.Credential,
        });
        limits["payloadPlaintextBytes"]!.GetValue<int>().ShouldBe(AgentSubmissionLimits.MaximumPayloadPlaintextBytes);
        limits["approverCount"]!.GetValue<int>().ShouldBe(AgentSubmissionLimits.MaximumApprovers);
        Contract["lifecycle"]!["trashRetentionDays"]!.GetValue<int>().ShouldBe(AgentSubmissionLimits.TrashRetentionDays);
    }

    private static IEnumerable<string> WireNames(Type type) => type
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Select(x => JsonNamingPolicy.CamelCase.ConvertName(x.Name))
        .Order(StringComparer.Ordinal);
}
