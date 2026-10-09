namespace MemberRegistration.Functions.Auth;

/// <summary>
/// App roles defined on this API's Entra ID app registration and granted to callers (the CRM's identity)
/// as application permissions. Each endpoint requires its own, so a caller can be allowed to preview the
/// legacy payload without being allowed to create members.
/// </summary>
public static class AppRoles
{
    public const string Translate = "Registrations.Translate";
    public const string Submit = "Registrations.Submit";
}
