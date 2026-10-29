namespace Palladin.Module.Vault.Domain;

internal static class AgentSubmissionLimits
{
    internal const int ContractVersion = 1;
    internal const int PayloadNonceBytes = 24;
    internal const int PayloadTagBytes = 16;
    internal const int MaximumPayloadPlaintextBytes = 32_768;
    internal const int MaximumPayloadBytes = PayloadNonceBytes + MaximumPayloadPlaintextBytes + PayloadTagBytes;
    internal const int MaximumDocumentLength = 49_152;
    internal const int MaximumApprovers = 256;
    internal const int TrashRetentionDays = 30;
}
