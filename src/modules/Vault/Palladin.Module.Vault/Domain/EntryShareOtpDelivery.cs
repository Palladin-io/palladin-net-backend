namespace Palladin.Module.Vault.Domain;

internal sealed record EntryShareOtpDelivery(long Generation, string ProtectedCode, string Language);
