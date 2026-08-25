namespace HendersonSoftwareLabsAPI.Entities;

public static class Roles
{
    public const string Admin = "Admin";
    public const string Client = "Client";

    public static bool IsAdmin(IEnumerable<string> roles) => roles.Contains(Admin);
}
