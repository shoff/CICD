namespace Cicd.Server.Security;

/// <summary>Validates post-login return URLs so the login endpoint can never redirect off-site.</summary>
public static class ReturnUrl
{
    /// <summary>True only for an absolute-path URL on this host: starts with a single '/', is not protocol-relative ('//' or '/\'), and has no control characters.</summary>
    public static bool IsLocal(string? url)
    {
        if (string.IsNullOrEmpty(url) || url[0] != '/')
        {
            return false;
        }
        if (url.Length > 1 && (url[1] == '/' || url[1] == '\\'))
        {
            return false;
        }
        foreach (var c in url)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The URL when it is local, otherwise "/".</summary>
    public static string Sanitize(string? url) => IsLocal(url) ? url! : "/";
}
