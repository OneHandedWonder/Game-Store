namespace Game_Store_API.Auth;

public static class AppRoles
{
    public const string Default = "default";
    public const string Employee = "employee";
    public const string Admin = "admin";
    public const string Staff = Employee + "," + Admin;

    public static bool IsValid(string? role) => role is Default or Employee or Admin;
}
