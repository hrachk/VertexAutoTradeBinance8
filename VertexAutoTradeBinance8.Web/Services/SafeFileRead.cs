namespace VertexAutoTradeBinance8.Web.Services;

/// <summary>Read files while bot process may write (no exclusive lock).</summary>
public static class SafeFileRead
{
    public static string? ReadAllTextShared(string path)
    {
        if (!File.Exists(path)) return null;
        using var fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }
}
