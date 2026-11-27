using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DungeonMapper.Models;

/// The tools' text (user, Nov 27): each tool's toolbar tooltip, side panel
/// hint and status bar line live in Assets/tool_text.json instead of the
/// code, so the wording can change without touching it. Keyed by tool
/// ("door"), then field ("tooltip", "hint", "status", and variants such as
/// "hint_player"). A value is a string or a list of lines. {key} is the
/// tool's current hotkey; other {names} are filled in by the caller. A
/// missing entry gives "" (the tooltip falls back to the tool's name), so
/// nothing breaks while text is being written.
public static class ToolText
{
    public const string AssetUri = "avares://DungeonMapper/Assets/tool_text.json";

    private static Dictionary<string, Dictionary<string, string>>? _text;

    /// the loaded text (loaded once, on first use)
    private static Dictionary<string, Dictionary<string, string>> Text => _text ??= LoadAsset();

    private static Dictionary<string, Dictionary<string, string>> LoadAsset()
    {
        try
        {
            using var stream = Avalonia.Platform.AssetLoader.Open(new Uri(AssetUri));
            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd());
        }
        catch
        {
            return new();
        }
    }

    /// reads the file's json: comments and trailing commas are allowed, a
    /// field can be a string or a list of lines (joined with line breaks)
    public static Dictionary<string, Dictionary<string, string>> Parse(string json)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        foreach (var tool in doc.RootElement.EnumerateObject())
        {
            if (tool.Value.ValueKind != JsonValueKind.Object) continue;
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in tool.Value.EnumerateObject())
            {
                if (f.Value.ValueKind == JsonValueKind.String) fields[f.Name] = f.Value.GetString() ?? "";
                else if (f.Value.ValueKind == JsonValueKind.Array)
                {
                    var lines = new List<string>();
                    foreach (var line in f.Value.EnumerateArray())
                        if (line.ValueKind == JsonValueKind.String) lines.Add(line.GetString() ?? "");
                    fields[f.Name] = string.Join("\n", lines);
                }
            }
            result[tool.Name] = fields;
        }
        return result;
    }

    /// replaces the loaded text (tests)
    public static void Use(Dictionary<string, Dictionary<string, string>> text) => _text = text;

    /// a tool's field with {key} and any other {name} filled in. "" when the
    /// file has no such text
    public static string Get(string tool, string field, string? hotkey = null, IReadOnlyDictionary<string, string>? values = null)
    {
        if (!Text.TryGetValue(tool, out var fields) || !fields.TryGetValue(field, out var text)) return "";
        return Fill(text, hotkey, values);
    }

    /// whether the file has the field (a variant falls back when it does not)
    public static bool Has(string tool, string field) =>
        Text.TryGetValue(tool, out var fields) && fields.ContainsKey(field);

    /// {key} becomes the hotkey; with no hotkey a "({key})" or "{key}" just
    /// goes, so "Door ({key})" reads "Door"
    public static string Fill(string text, string? hotkey, IReadOnlyDictionary<string, string>? values = null)
    {
        if (string.IsNullOrEmpty(hotkey))
            text = text.Replace(" ({key})", "").Replace("({key})", "").Replace("{key}", "");
        else
            text = text.Replace("{key}", hotkey);
        if (values is not null)
            foreach (var (name, value) in values) text = text.Replace("{" + name + "}", value);
        return text;
    }
}
