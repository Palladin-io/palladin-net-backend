namespace Palladin.Core.Security;

/// <summary>
/// Exempts a reviewed endpoint from the active-organization unsafe-method boundary.
/// Apply only to account/exit operations or semantically read-only POST endpoints that must remain
/// usable while the JWT organization membership is being removed.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AllowNonActiveOrganizationMembershipAttribute : Attribute;
