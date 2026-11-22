using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace WiseSphere.Core;

/// <summary>Text helpers shared by the event parsers and deciders.</summary>
public static partial class Text
{
    [GeneratedRegex(@"^\{[A-Z0-9_.$#]+(\.[^}]*)?\}$")]
    private static partial Regex UnresolvedMacro();

    /// <summary>Trim a value and drop unresolved Zabbix macros such as "{EVENT.TAGS.client}" or "*UNKNOWN*".</summary>
    public static string Clean(string? value)
    {
        if (value is null) return string.Empty;
        var s = value.Trim();
        if (s == "*UNKNOWN*" || UnresolvedMacro().IsMatch(s)) return string.Empty;
        return s;
    }

    public static string Truncate(string? value, int max)
    {
        value ??= string.Empty;
        return value.Length > max ? string.Concat(value.AsSpan(0, max - 1), "…") : value;
    }

    /// <summary>GLPI rich-text content: a title paragraph and "label: value" lines (empty values skipped).</summary>
    public static string HtmlRows(string title, IEnumerable<(string Label, string? Value)> rows, string? footer = null)
    {
        var sb = new StringBuilder();
        sb.Append("<p>").Append(WebUtility.HtmlEncode(title)).Append("</p><p>");
        var first = true;
        foreach (var (label, value) in rows)
        {
            if (string.IsNullOrEmpty(value)) continue;
            if (!first) sb.Append("<br>");
            sb.Append("<b>").Append(WebUtility.HtmlEncode(label)).Append(":</b> ").Append(WebUtility.HtmlEncode(value));
            first = false;
        }
        sb.Append("</p>");
        if (footer is not null) sb.Append("<p><i>").Append(WebUtility.HtmlEncode(footer)).Append("</i></p>");
        return sb.ToString();
    }
}
