using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using MerchantIntelligence.Kyb.Matching;

namespace MerchantIntelligence.Kyb.Registry;

/// <summary>
/// Kentucky Secretary of State business search (sosbes.sos.ky.gov). Free and keyless; the only state register that
/// answers for a Kentucky LLC, corporation or assumed name. There is no API – the provider drives the public search form
/// (one search post, then one profile fetch per candidate) and reads the rendered page. Only used for US applicants in KY.
/// </summary>
public sealed partial class KentuckySosRegistryProvider : IBusinessRegistryProvider
{
    public const string BaseUrl = "https://sosbes.sos.ky.gov/BusSearchNProfile/";
    private const string SearchUrl = BaseUrl + "search.aspx";

    private readonly IHttpClientFactory _factory;
    private readonly KybOptions _options;

    public KentuckySosRegistryProvider(IHttpClientFactory factory, KybOptions options)
    {
        _factory = factory;
        _options = options;
    }

    public string Name => "Kentucky SOS";
    public RegistryReach Reach => RegistryReach.Local;
    public bool IsEnabled => _options.StateRegistriesEnabled;

    public bool Covers(BusinessIdentity identity) =>
        (string.IsNullOrWhiteSpace(identity.Country) || identity.Country.Equals("US", StringComparison.OrdinalIgnoreCase))
        && identity.Region is { } r
        && (r.Trim().Equals("KY", StringComparison.OrdinalIgnoreCase) || r.Trim().Equals("Kentucky", StringComparison.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<RegistryRecord>> SearchAsync(BusinessIdentity identity, CancellationToken ct)
    {
        var client = _factory.CreateClient(KybOptions.HttpClientName);
        var found = new Dictionary<string, SearchHit>(StringComparer.Ordinal);
        foreach (var query in QueriesFor(identity))
        {
            var form = await GetFormAsync(client, ct);
            form["ctl00$MainContent$ddlSearchBy"] = "Business Name or Organization Number";
            form["ctl00$MainContent$txtSearch"] = query;
            form["ctl00$MainContent$BSearch"] = "Search";
            using var request = new HttpRequestMessage(HttpMethod.Post, SearchUrl) { Content = new FormUrlEncodedContent(form) };
            request.Headers.Accept.ParseAdd("text/html");
            using var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            foreach (var hit in ParseSearchResults(await response.Content.ReadAsStringAsync(ct)))
                found.TryAdd(hit.OrganizationNumber + "|" + hit.Name, hit);
            if (found.Count > 0) break;
        }

        var declared = new[] { identity.LegalName, identity.TradingName }.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        var candidates = found.Values
            .OrderByDescending(h => declared.Max(n => NameMatcher.Similarity(n!, h.Name)))
            .Take(_options.MaxResultsPerSource)
            .ToList();

        var list = new List<RegistryRecord>();
        foreach (var hit in candidates)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + hit.ProfilePath);
            request.Headers.Accept.ParseAdd("text/html");
            using var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var profile = ParseProfile(await response.Content.ReadAsStringAsync(ct));
            list.Add(ToRecord(hit, profile));
        }
        return list;
    }

    /// <summary>
    /// The register's search is token-based: "Riverbend Grill" finds nothing while "Riverbend" finds "Riverbend MEAT &amp; GRILL LLC".
    /// Try the declared names first, then progressively shorter distinctive prefixes and the joined form of the first words.
    /// </summary>
    internal static IReadOnlyList<string> QueriesFor(BusinessIdentity identity)
    {
        var queries = new List<string>();
        void Add(string? q)
        {
            if (string.IsNullOrWhiteSpace(q)) return;
            q = q.Trim();
            if (q.Length >= 3 && !queries.Contains(q, StringComparer.OrdinalIgnoreCase)) queries.Add(q);
        }

        if (!string.IsNullOrWhiteSpace(identity.RegistrationNumber) && identity.RegistrationNumber.All(char.IsDigit))
            Add(identity.RegistrationNumber);
        foreach (var name in new[] { identity.LegalName, identity.TradingName })
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            Add(name);
            var tokens = SignificantTokens(name);
            if (tokens.Count >= 2)
            {
                Add(string.Join(' ', tokens.Take(2)));
                Add(string.Concat(tokens.Take(2)));
            }
            if (tokens.Count >= 1) Add(tokens[0]);
        }
        return queries.Take(6).ToList();
    }

    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "llc", "l.l.c.", "inc", "inc.", "corp", "corp.", "corporation", "co", "co.", "company", "ltd", "ltd.", "limited",
        "lp", "llp", "pllc", "psc", "the", "and", "&", "of", "dba", "d/b/a"
    };

    internal static List<string> SignificantTokens(string name) =>
        name.Split(new[] { ' ', ',', '-', '/' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !Noise.Contains(t) && t.Length >= 2)
            .ToList();

    private static async Task<Dictionary<string, string>> GetFormAsync(HttpClient client, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, SearchUrl);
        request.Headers.Accept.ParseAdd("text/html");
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(ct);
        var form = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in HiddenField().Matches(html))
            form[m.Groups["name"].Value] = WebUtility.HtmlDecode(m.Groups["value"].Value);
        return form;
    }

    internal sealed record SearchHit(string Name, string OrganizationNumber, string Status, string Type, string ProfilePath);

    internal static IReadOnlyList<SearchHit> ParseSearchResults(string html)
    {
        var hits = new List<SearchHit>();
        foreach (Match row in TableRow().Matches(html))
        {
            var cells = TableCell().Matches(row.Value).Select(c => Clean(c.Groups["cell"].Value)).ToList();
            if (cells.Count < 4 || cells[1].Length == 0 || !cells[1].All(char.IsDigit)) continue;
            var link = ProfileLink().Match(row.Value);
            if (!link.Success) continue;
            hits.Add(new SearchHit(cells[0], cells[1], cells[2], cells[3], WebUtility.HtmlDecode(link.Groups["path"].Value)));
        }
        return hits;
    }

    internal static IReadOnlyDictionary<string, string> ParseProfile(string html)
    {
        var text = Clean(ScriptBlock().Replace(html, " "), keepLines: true);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var start = lines.FindIndex(l => l.Equals("General Information", StringComparison.OrdinalIgnoreCase));
        if (start < 0) return fields;
        for (var i = start + 1; i < lines.Count; i++)
        {
            var label = lines[i];
            if (!ProfileLabels.Contains(label)) continue;
            var value = new List<string>();
            for (var j = i + 1; j < lines.Count && !ProfileLabels.Contains(lines[j]) && !lines[j].StartsWith("Loading", StringComparison.Ordinal); j++)
                value.Add(lines[j]);
            if (value.Count > 0) fields[label] = string.Join(", ", value);
            i += value.Count;
        }
        return fields;
    }

    private static readonly HashSet<string> ProfileLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "Organization Number", "Name", "Profit or Non-Profit", "Company Type", "Industry", "Number of Employees", "Primary County",
        "Status", "Standing", "State", "Country", "File Date", "Organization Date", "Last Annual Report", "Principal Office",
        "Managed By", "Registered Agent", "Authority Date", "Expiration Date", "Assumed Name", "Real Name"
    };

    private RegistryRecord ToRecord(SearchHit hit, IReadOnlyDictionary<string, string> p)
    {
        DateOnly? inc = null;
        foreach (var key in new[] { "Organization Date", "File Date", "Authority Date" })
        {
            if (p.TryGetValue(key, out var d) && DateOnly.TryParse(d, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dd)) { inc = dd; break; }
        }

        var extra = new Dictionary<string, string>();
        void Copy(string label, string key) { if (p.TryGetValue(label, out var v) && v != "N/A") extra[key] = v; }
        Copy("Standing", "standing");
        Copy("Industry", "industry");
        Copy("Number of Employees", "employeeBand");
        Copy("Primary County", "county");
        Copy("Last Annual Report", "lastAnnualReport");
        Copy("Managed By", "managedBy");
        Copy("Registered Agent", "registeredAgent");
        Copy("Profit or Non-Profit", "profitStatus");
        if (hit.Type.Contains("Assumed Name", StringComparison.OrdinalIgnoreCase)) extra["assumedName"] = hit.Name;

        var status = p.TryGetValue("Status", out var st) ? st : hit.Status;
        var type = p.TryGetValue("Company Type", out var ctype) ? ctype : hit.Type;
        var legalName = p.TryGetValue("Name", out var n) && !string.IsNullOrWhiteSpace(n) ? n : hit.Name;
        return new RegistryRecord(Name, hit.OrganizationNumber, legalName, status, inc, "US-KY", hit.OrganizationNumber,
            p.TryGetValue("Principal Office", out var office) ? office : null, type,
            new Uri(BaseUrl + hit.ProfilePath), extra);
    }

    private static string Clean(string html, bool keepLines = false)
    {
        var text = Tag().Replace(html, keepLines ? "\n" : " ");
        text = WebUtility.HtmlDecode(text).Replace('\u00a0', ' ');
        return keepLines
            ? string.Join('\n', text.Split('\n').Select(l => Spaces().Replace(l, " ").Trim()).Where(l => l.Length > 0))
            : Spaces().Replace(text, " ").Trim();
    }

    [GeneratedRegex(@"<input[^>]*name=""(?<name>__[A-Z]+)""[^>]*value=""(?<value>[^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenField();

    [GeneratedRegex(@"<tr[^>]*>.*?</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TableRow();

    [GeneratedRegex(@"<td[^>]*>(?<cell>.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TableCell();

    [GeneratedRegex(@"href=""(?<path>Profile\.aspx[^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex ProfileLink();

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptBlock();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t\r]+")]
    private static partial Regex Spaces();
}
