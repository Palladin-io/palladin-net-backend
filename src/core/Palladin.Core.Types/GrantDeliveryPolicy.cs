namespace Palladin.Core.Types;

public enum GrantDeliveryPolicy : ushort
{
    Standard = 0,
    ExecOnly = 1,
}

public static class GrantDeliveryPolicyExtensions
{
    public static bool IsValid(this GrantDeliveryPolicy policy) =>
        policy is GrantDeliveryPolicy.Standard or GrantDeliveryPolicy.ExecOnly;
}
