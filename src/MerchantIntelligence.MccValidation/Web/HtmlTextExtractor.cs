using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace MerchantIntelligence.MccValidation.Web;

public sealed record ExtractedPage(
    string Title,
    string MetaDescription,
    IReadOnlyList<string> Headings,
    IReadOnlyList<string> SchemaOrgTypes,
    string BodyText,
    IReadOnlyList<Uri> InternalLinks)
{
    /// <summary>Title, description and headings up front so they dominate the feature space.</summary>
    public string ToClassifierText() =>
        string.Join(" ", new[] { Title, MetaDescription, string.Join(" ", Headings), BodyText }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>
/// Pulls the human-visible text and light structural signals out of an HTML page.
/// </summary>
public static class HtmlTextExtractor
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly HashSet<string> SkippedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "svg", "template", "iframe", "head", "nav", "footer"
    };

    public static ExtractedPage Extract(string html, Uri? baseUri = null, int maxChars = 20_000)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var title = Clean(doc.DocumentNode.SelectSingleNode("//title")?.InnerText);
        var meta = Clean(
            doc.DocumentNode.SelectSingleNode("//meta[@name='description']")?.GetAttributeValue("content", null)
            ?? doc.DocumentNode.SelectSingleNode("//meta[@property='og:description']")?.GetAttributeValue("content", null));

        var headings = doc.DocumentNode.SelectNodes("//h1|//h2|//h3")
            ?.Select(h => Clean(h.InnerText))
            .Where(h => h.Length > 0)
            .Distinct()
            .Take(40)
            .ToList() ?? new List<string>();

        var schemaTypes = ExtractSchemaOrgTypes(doc);

        var sb = new StringBuilder();
        var body = doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;
        AppendText(body, sb, maxChars);
        var bodyText = Clean(sb.ToString());
        if (bodyText.Length > maxChars) bodyText = bodyText[..maxChars];

        var links = baseUri is null ? new List<Uri>() : ExtractInternalLinks(doc, baseUri);

        return new ExtractedPage(title, meta, headings, schemaTypes, bodyText, links);
    }

    private static void AppendText(HtmlNode node, StringBuilder sb, int maxChars)
    {
        if (sb.Length >= maxChars) return;
        if (node.NodeType == HtmlNodeType.Comment) return;
        if (node.NodeType == HtmlNodeType.Text)
        {
            var text = HtmlEntity.DeEntitize(node.InnerText);
            if (!string.IsNullOrWhiteSpace(text)) sb.Append(text).Append(' ');
            return;
        }

        if (SkippedTags.Contains(node.Name)) return;
        foreach (var child in node.ChildNodes) AppendText(child, sb, maxChars);
    }

    private static List<string> ExtractSchemaOrgTypes(HtmlDocument doc)
    {
        var types = new List<string>();
        var scripts = doc.DocumentNode.SelectNodes("//script[@type='application/ld+json']");
        if (scripts is null) return types;

        foreach (var script in scripts)
        {
            try
            {
                using var json = JsonDocument.Parse(script.InnerText);
                CollectTypes(json.RootElement, types);
            }
            catch (JsonException)
            {
                // Malformed JSON-LD is common in the wild; ignore it.
            }
        }

        return types.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void CollectTypes(JsonElement element, List<string> types)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("@type", out var t))
                {
                    if (t.ValueKind == JsonValueKind.String) types.Add(t.GetString()!);
                    else if (t.ValueKind == JsonValueKind.Array)
                        types.AddRange(t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
                }
                foreach (var prop in element.EnumerateObject()) CollectTypes(prop.Value, types);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) CollectTypes(item, types);
                break;
        }
    }

    private static List<Uri> ExtractInternalLinks(HtmlDocument doc, Uri baseUri)
    {
        var anchors = doc.DocumentNode.SelectNodes("//a[@href]");
        if (anchors is null) return new List<Uri>();

        return anchors
            .Select(a => a.GetAttributeValue("href", string.Empty))
            .Where(h => h.Length > 0 && !h.StartsWith('#') && !h.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                        && !h.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) && !h.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
            .Select(h => Uri.TryCreate(baseUri, h, out var u) ? u : null)
            .Where(u => u is not null && u.Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase))
            .Select(u => u!)
            .Distinct()
            .ToList();
    }

    private static string Clean(string? text) =>
        text is null ? string.Empty : Whitespace.Replace(HtmlEntity.DeEntitize(text), " ").Trim();
}
