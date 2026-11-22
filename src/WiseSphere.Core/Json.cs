using System.Text.Json;

namespace WiseSphere.Core;

/// <summary>Tolerant JSON readers for webhook payloads (values may arrive as strings or numbers).</summary>
public static class Json
{
    public static string? Str(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
            JsonValueKind.Array or JsonValueKind.Object => v.GetRawText(),
            _ => null,
        };
    }

    public static JsonElement Obj(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    public static int? Number(JsonElement obj, string name) =>
        int.TryParse(Str(obj, name), out var n) ? n : null;

    /// <summary>Parse Zabbix {EVENT.TAGSJSON} ([{tag, value}]) into a dictionary; first value wins; tolerant of bad input.</summary>
    public static Dictionary<string, string> ParseTags(string? raw)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        raw = Text.Clean(raw);
        if (raw.Length == 0) return result;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                var tag = Str(t, "tag");
                if (tag is not null && !result.ContainsKey(tag)) result[tag] = Str(t, "value") ?? "";
            }
        }
        catch (JsonException)
        {
            // malformed tags are ignored; the event still produces a ticket (flagged UNMAPPED if no client)
        }
        return result;
    }
}
