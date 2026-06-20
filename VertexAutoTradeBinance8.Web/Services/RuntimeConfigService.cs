using System.Text.Json;
using System.Text.Json.Nodes;

namespace VertexAutoTradeBinance8.Web.Services;

/// <summary>
/// Reads and writes appsettings.runtime.json — the live-reload overlay
/// file the Engine's Host.ConfigureAppConfiguration layers on top of its
/// base appsettings.json (see Program.cs in the Engine project, the
/// AddJsonFile(..., reloadOnChange: true) call added for v9).
///
/// Design choice: this works on raw JsonNode trees rather than strongly
/// typed C# models for every section. Reasons:
///   1) appsettings.json has 15+ sections with deeply nested sub-objects
///      (SignalConfidence.BTC.Bands.MediumFrom, etc) — modeling every one
///       individually for the Settings page would be a huge surface to
///      keep in sync with the Engine's own Configuration/*.cs classes.
///   2) The overlay file only needs to contain a SPARSE subset — whatever
///      the person actually changed. A raw JSON merge naturally supports
///      "only override what's set", whereas a strongly-typed object would
///      either need every property nullable or always write the full tree.
///   3) The Engine's OWN consumers (TradingOptionsResolver, IConfiguration.
///      GetValue, etc.) bind from the merged IConfiguration regardless of
///      whether this file contains one key or fifty — there's no need for
///      the Web side to mirror those C# classes exactly, only the JSON
///      shape has to match.
///
/// Read merges the override file on top of a read-only view of the base
/// appsettings.json (fetched once at startup time values, not live —
/// good enough for "what is currently configured" display purposes).
/// Write does a targeted JsonNode merge: only the path being changed is
/// touched, everything else in the override file is left untouched.
/// </summary>
public sealed class RuntimeConfigService
{
    private readonly string _runtimePath;
    private readonly string _baseAppSettingsPath;
    private readonly ILogger<RuntimeConfigService> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public RuntimeConfigService(IConfiguration cfg, ILogger<RuntimeConfigService> logger)
    {
        _logger = logger;

        var root = cfg["SharedData:Root"];
        if (string.IsNullOrWhiteSpace(root))
            root = AppContext.BaseDirectory;

        _runtimePath = Path.Combine(root, "appsettings.runtime.json");

        // Best-effort path to the Engine's base appsettings.json, purely
        // for the "current effective value" display — if it can't be
        // found (different deployment layout), the page still works
        // using only the override file's contents.
        _baseAppSettingsPath = cfg["Engine:AppSettingsPath"]
            ?? Path.Combine(root, "appsettings.json");
    }

    /// <summary>
    /// Returns the merged configuration tree: base appsettings.json values
    /// with the runtime override file layered on top (matching exactly
    /// what the Engine's own IConfiguration sees at any given moment).
    /// </summary>
    public async Task<JsonObject> GetEffectiveConfigAsync()
    {
        JsonObject merged;

        if (File.Exists(_baseAppSettingsPath))
        {
            var baseJson = await ReadJsonFileStrippingCommentsAsync(_baseAppSettingsPath);
            merged = baseJson ?? new JsonObject();
        }
        else
        {
            merged = new JsonObject();
        }

        if (File.Exists(_runtimePath))
        {
            var overrideJson = await ReadJsonFileStrippingCommentsAsync(_runtimePath);
            if (overrideJson != null)
                DeepMerge(merged, overrideJson);
        }

        return merged;
    }

    /// <summary>
    /// Returns just the override file's own contents (what's actually
    /// been changed via the Settings page so far), without the base
    /// appsettings.json values merged in. Useful for showing "modified"
    /// indicators in the UI.
    /// </summary>
    public async Task<JsonObject> GetOverridesOnlyAsync()
    {
        if (!File.Exists(_runtimePath)) return new JsonObject();
        return await ReadJsonFileStrippingCommentsAsync(_runtimePath) ?? new JsonObject();
    }

    /// <summary>
    /// Sets a single value at a colon-delimited config path (matching
    /// IConfiguration's own path convention, e.g. "Trading:Leverage" or
    /// "SignalConfidence:BTC:Bands:MediumFrom"), creating intermediate
    /// objects as needed, and atomically writes the override file.
    /// </summary>
    public async Task SetValueAsync(string configPath, JsonNode? value)
    {
        await _writeLock.WaitAsync();
        try
        {
            var root = File.Exists(_runtimePath)
                ? await ReadJsonFileStrippingCommentsAsync(_runtimePath) ?? new JsonObject()
                : new JsonObject();

            var segments = configPath.Split(':', StringSplitOptions.RemoveEmptyEntries);
            JsonObject current = root;

            for (int i = 0; i < segments.Length - 1; i++)
            {
                var key = segments[i];
                if (current[key] is not JsonObject child)
                {
                    child = new JsonObject();
                    current[key] = child;
                }
                current = child;
            }

            current[segments[^1]] = value;

            await WriteAtomicAsync(root);

            _logger.LogInformation("[RuntimeConfig] {path} = {value}", configPath, value?.ToJsonString() ?? "null");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Convenience overload for plain scalar values (numbers, strings,
    /// bools) without the caller needing to construct JsonValue manually.
    /// </summary>
    public Task SetValueAsync(string configPath, object? scalarValue)
    {
        JsonNode? node = scalarValue switch
        {
            null => null,
            bool b => JsonValue.Create(b),
            int i => JsonValue.Create(i),
            long l => JsonValue.Create(l),
            decimal d => JsonValue.Create(d),
            double db => JsonValue.Create(db),
            string s => JsonValue.Create(s),
            _ => JsonValue.Create(scalarValue.ToString())
        };
        return SetValueAsync(configPath, node);
    }

    /// <summary>
    /// Replaces an entire array at the given path (used for Pinned symbol
    /// lists, ToxicSymbols, etc — list editing is "replace the whole list"
    /// rather than per-item patching).
    /// </summary>
    public async Task SetArrayAsync(string configPath, IEnumerable<string> items)
    {
        var arr = new JsonArray();
        foreach (var item in items)
            arr.Add(JsonValue.Create(item));

        await SetValueAsync(configPath, arr);
    }

    /// <summary>
    /// Removes a runtime override for a path, falling back to whatever
    /// the base appsettings.json has for that key.
    /// </summary>
    public async Task ClearOverrideAsync(string configPath)
    {
        await _writeLock.WaitAsync();
        try
        {
            if (!File.Exists(_runtimePath)) return;

            var root = await ReadJsonFileStrippingCommentsAsync(_runtimePath) ?? new JsonObject();
            var segments = configPath.Split(':', StringSplitOptions.RemoveEmptyEntries);

            JsonObject? current = root;
            for (int i = 0; i < segments.Length - 1 && current != null; i++)
                current = current[segments[i]] as JsonObject;

            current?.Remove(segments[^1]);

            await WriteAtomicAsync(root);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task WriteAtomicAsync(JsonObject root)
    {
        var dir = Path.GetDirectoryName(_runtimePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmpPath = _runtimePath + ".tmp";
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        await File.WriteAllTextAsync(tmpPath, json);
        File.Move(tmpPath, _runtimePath, overwrite: true);
    }

    private async Task<JsonObject?> ReadJsonFileStrippingCommentsAsync(string path)
    {
        try
        {
            string text;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))
                text = await sr.ReadToEndAsync();

            if (string.IsNullOrWhiteSpace(text)) return new JsonObject();

            // appsettings.json in this repo uses // line comments, which
            // is non-standard JSON but supported by .NET's configuration
            // provider via JsonDocumentOptions. JsonNode.Parse needs the
            // same allowance explicitly.
            var options = new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };

            return JsonNode.Parse(text, documentOptions: options) as JsonObject;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[RuntimeConfig] failed to parse {path}", path);
            return null;
        }
    }

    private static void DeepMerge(JsonObject target, JsonObject source)
    {
        foreach (var kvp in source)
        {
            if (kvp.Value is JsonObject sourceChild &&
                target[kvp.Key] is JsonObject targetChild)
            {
                DeepMerge(targetChild, sourceChild);
            }
            else
            {
                // JsonNode instances can only belong to one parent at a
                // time — clone via round-trip serialization before
                // attaching to the target tree to avoid "node already
                // has a parent" exceptions.
                target[kvp.Key] = kvp.Value != null
                    ? JsonNode.Parse(kvp.Value.ToJsonString())
                    : null;
            }
        }
    }
}
