using System.Text.Json;
using System.Text.Json.Nodes;
using Palladin.Module.Vault.Shared;

namespace Palladin.Tests.Integrations.Features.Vault.AgentSubmissions;

// Deterministic vectors vendored from palladin-protocol contracts/agent-submission/v1/fixture.json.
internal sealed class AgentSubmissionFixture
{
    private static readonly Lazy<AgentSubmissionFixture> Instance = new(Load);

    private AgentSubmissionFixture(JsonNode root) => Root = root;

    internal static AgentSubmissionFixture Current => Instance.Value;

    internal JsonNode Root { get; }
    internal JsonNode Context => Root["context"]!;
    internal Guid OrganizationId => Guid.Parse(Context["organizationId"]!.GetValue<string>());
    internal Guid AgentId => Guid.Parse(Context["agentId"]!.GetValue<string>());
    internal Guid MemberId => Guid.Parse(Context["memberId"]!.GetValue<string>());
    internal uint AgentKeyVersion => Context["agentKeyVersion"]!.GetValue<uint>();
    internal uint AgentAccessEpoch => uint.Parse(Context["agentAccessEpoch"]!.GetValue<string>());
    internal uint MemberKeyVersion => Context["memberKeyVersion"]!.GetValue<uint>();
    internal byte[] AgentX25519PublicKey => Decode(Context["agentX25519PublicKey"]!);
    internal byte[] AgentEd25519PublicKey => Decode(Context["agentEd25519PublicKey"]!);
    internal byte[] MemberX25519PublicKey => Decode(Context["memberX25519PublicKey"]!);
    internal byte[] AgentEd25519Seed => Convert.FromHexString(Root["deterministicInputs"]!["agentEd25519Seed"]!.GetValue<string>());
    internal Guid VaultId => Guid.Parse(Submission(0).Target.VaultId!);
    internal Guid EntryId => Guid.Parse(Submission(0).Target.EntryId!);

    internal OrganizationApproverManifestContract Manifest(int index) =>
        Root["approverManifests"]![index].Deserialize<OrganizationApproverManifestContract>(JsonSerializerOptions.Web)!;

    internal string ManifestJson(int index) => Root["approverManifests"]![index]!.ToJsonString();

    internal AgentEntrySubmissionContract Submission(int index) =>
        Root["submissions"]![index].Deserialize<AgentEntrySubmissionContract>(JsonSerializerOptions.Web)!;

    internal string SubmissionJson(int index) => Root["submissions"]![index]!.ToJsonString();

    internal IReadOnlyList<ApproverKeyWrapContract> ApproverKeyWraps =>
        Root["approverKeyWraps"].Deserialize<List<ApproverKeyWrapContract>>(JsonSerializerOptions.Web)!;

    private static byte[] Decode(JsonNode node) =>
        Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(node.GetValue<string>());

    private static AgentSubmissionFixture Load() => new(JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "AgentSubmission", "fixture-v1.json")))!);
}
