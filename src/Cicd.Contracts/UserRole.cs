namespace Cicd.Contracts;

/// <summary>Global roles, ordered from least to most privileged. Comparisons rely on this order.</summary>
public enum UserRole
{
    Viewer = 0,
    Developer = 1,
    Admin = 2,
}
