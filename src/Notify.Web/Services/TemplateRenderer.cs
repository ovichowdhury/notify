using System.Net;
using System.Text.RegularExpressions;

namespace Notify.Web.Services;

/// <summary>
/// Replaces {{Column}} placeholders with values from the recipient row.
/// Placeholder names are matched case-insensitively against the uploaded file's columns.
/// </summary>
public static partial class TemplateRenderer
{
    [GeneratedRegex(@"\{\{\s*([^{}]+?)\s*\}\}", RegexOptions.Compiled)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"<(br|/p|/div|/tr|/li|/h[1-6])\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LineBreakTags();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex ScriptAndStyle();

    public static string Render(string? template, IReadOnlyDictionary<string, string> data, bool htmlEncode)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;
        return Placeholder().Replace(template, m =>
        {
            var key = m.Groups[1].Value.Trim();
            if (!data.TryGetValue(key, out var value) || value is null) return string.Empty;
            return htmlEncode ? WebUtility.HtmlEncode(value) : value;
        });
    }

    public static IReadOnlyList<string> ExtractPlaceholders(string? template)
    {
        if (string.IsNullOrEmpty(template)) return Array.Empty<string>();
        return Placeholder().Matches(template)
            .Select(m => m.Groups[1].Value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string ToPlainText(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var text = ScriptAndStyle().Replace(html, string.Empty);
        text = LineBreakTags().Replace(text, "\n");
        text = AnyTag().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
        return string.Join("\n", lines);
    }
}
