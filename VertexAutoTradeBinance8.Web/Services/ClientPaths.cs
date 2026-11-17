namespace VertexAutoTradeBinance8.Web.Services;

/// <summary>
/// One folder per registered user. Ids are already "client_001",
/// so a second "client_" prefix must not be added.
/// </summary>
public static class ClientPaths
{
    public static string Normalize(string? clientId)
    {
        var id = (clientId ?? "").Trim();
        while (id.StartsWith("client_client_", StringComparison.OrdinalIgnoreCase))
            id = id["client_".Length..];
        return id;
    }

    public static string FolderName(string? clientId)
    {
        var id = Normalize(clientId);
        if (string.IsNullOrEmpty(id)) return "anonymous";
        if (id.StartsWith("client_", StringComparison.OrdinalIgnoreCase))
            return id;
        return "client_" + id;
    }

    /// <summary>
    /// Directory for this user. If an older build wrote client_client_001,
    /// keep reading that folder until the canonical one exists.
    /// </summary>
    public static string Dir(string enginesRoot, string? clientId)
    {
        var folder = FolderName(clientId);
        var dir = Path.Combine(enginesRoot, folder);
        var doubled = Path.Combine(enginesRoot, "client_" + folder);
        if (!File.Exists(Path.Combine(dir, "demo-account.json"))
            && File.Exists(Path.Combine(doubled, "demo-account.json")))
            return doubled;
        return dir;
    }
}
