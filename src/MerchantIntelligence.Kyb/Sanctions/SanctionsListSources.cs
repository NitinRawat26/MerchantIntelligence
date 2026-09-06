using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MerchantIntelligence.Kyb.Sanctions;

public interface ISanctionsListSource
{
    string ListName { get; }
    string DownloadUrl { get; }
    string CacheFileName { get; }
    IEnumerable<SanctionedEntity> Parse(Stream content);
}

/// <summary>
/// OpenSanctions consolidated sanctions dataset (OFAC, EU, UN, UK HMT, and ~60 more lists).
/// Free bulk download, CC BY-NC 4.0 for non-commercial use; commercial use requires a licence.
/// </summary>
public sealed class OpenSanctionsSource : ISanctionsListSource
{
    private readonly string _dataset;

    public OpenSanctionsSource(string dataset = "sanctions") => _dataset = dataset;

    public string ListName => $"OpenSanctions/{_dataset}";
    public string DownloadUrl => $"https://data.opensanctions.org/datasets/latest/{_dataset}/targets.simple.csv";
    public string CacheFileName => $"opensanctions-{_dataset}.csv";

    public IEnumerable<SanctionedEntity> Parse(Stream content)
    {
        using var reader = new StreamReader(content);
        string[]? header = null;
        var idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in CsvReader.Read(reader))
        {
            if (header is null)
            {
                header = row;
                for (var i = 0; i < header.Length; i++) idx[header[i]] = i;
                continue;
            }
            if (row.Length < header.Length) continue;

            string Get(string col) => idx.TryGetValue(col, out var i) && i < row.Length ? row[i] : string.Empty;
            static IReadOnlyList<string> Split(string v) =>
                string.IsNullOrWhiteSpace(v) ? Array.Empty<string>() : v.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var id = Get("id");
            var name = Get("name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            yield return new SanctionedEntity(
                id,
                ListName,
                MapSchema(Get("schema")),
                name,
                Split(Get("aliases")),
                Split(Get("birth_date")),
                Split(Get("countries")),
                Split(Get("sanctions")).Concat(Split(Get("program_ids"))).Distinct().ToList(),
                Get("dataset"),
                Uri.TryCreate($"https://www.opensanctions.org/entities/{id}/", UriKind.Absolute, out var u) ? u : null);
        }
    }

    private static SanctionedEntityType MapSchema(string schema) => schema switch
    {
        "Person" => SanctionedEntityType.Person,
        "Organization" or "Company" or "LegalEntity" or "PublicBody" => SanctionedEntityType.Organization,
        "Vessel" => SanctionedEntityType.Vessel,
        "Airplane" => SanctionedEntityType.Aircraft,
        _ => SanctionedEntityType.Unknown
    };
}

/// <summary>US Treasury OFAC Specially Designated Nationals list, raw CSV export (authoritative, public domain).</summary>
public sealed class OfacSdnSource : ISanctionsListSource
{
    private static readonly Regex Dob = new(@"DOB\s+([^;]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Nationality = new(@"(?:nationality|citizen)\s+([^;]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Aka = new(@"a\.k\.a\.\s+'([^']+)'", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string ListName => "OFAC SDN";
    public string DownloadUrl => "https://sanctionslistservice.ofac.treas.gov/api/PublicationPreview/exports/SDN.CSV";
    public string CacheFileName => "ofac-sdn.csv";

    public IEnumerable<SanctionedEntity> Parse(Stream content)
    {
        using var reader = new StreamReader(content);
        foreach (var row in CsvReader.Read(reader))
        {
            if (row.Length < 4 || !int.TryParse(row[0].Trim(), out var entNum)) continue;
            static string? Clean(string v) { v = v.Trim(); return v == "-0-" || v.Length == 0 ? null : v; }
            var name = Clean(row[1]);
            if (name is null) continue;
            var type = Clean(row[2]);
            var remarks = row.Length > 11 ? Clean(row[11]) : null;
            var programs = Clean(row[3])?.Split(new[] { "] [", "[", "]", ";" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>();
            var dobs = remarks is null ? Array.Empty<string>() : Dob.Matches(remarks).Select(m => m.Groups[1].Value.Trim()).ToArray();
            var countries = remarks is null ? Array.Empty<string>() : Nationality.Matches(remarks).Select(m => m.Groups[1].Value.Trim()).ToArray();
            var aliases = remarks is null ? Array.Empty<string>() : Aka.Matches(remarks).Select(m => m.Groups[1].Value.Trim()).ToArray();
            yield return new SanctionedEntity(
                $"OFAC-{entNum}", ListName,
                type is null ? SanctionedEntityType.Organization
                    : type.Equals("individual", StringComparison.OrdinalIgnoreCase) ? SanctionedEntityType.Person
                    : type.Equals("vessel", StringComparison.OrdinalIgnoreCase) ? SanctionedEntityType.Vessel
                    : type.Equals("aircraft", StringComparison.OrdinalIgnoreCase) ? SanctionedEntityType.Aircraft
                    : SanctionedEntityType.Organization,
                name, aliases, dobs, countries, programs, remarks,
                new Uri($"https://sanctionssearch.ofac.treas.gov/Details.aspx?id={entNum}"));
        }
    }
}

/// <summary>United Nations Security Council consolidated list (XML, public).</summary>
public sealed class UnConsolidatedSource : ISanctionsListSource
{
    public string ListName => "UN Security Council";
    public string DownloadUrl => "https://scsanctions.un.org/resources/xml/en/consolidated.xml";
    public string CacheFileName => "un-consolidated.xml";

    public IEnumerable<SanctionedEntity> Parse(Stream content)
    {
        var doc = XDocument.Load(content);
        foreach (var ind in doc.Descendants("INDIVIDUAL"))
        {
            var name = string.Join(' ', new[] { "FIRST_NAME", "SECOND_NAME", "THIRD_NAME", "FOURTH_NAME" }
                .Select(t => (string?)ind.Element(t)).Where(s => !string.IsNullOrWhiteSpace(s)));
            if (name.Length == 0) continue;
            var id = (string?)ind.Element("DATAID") ?? (string?)ind.Element("REFERENCE_NUMBER") ?? Guid.NewGuid().ToString("N");
            var aliases = ind.Elements("INDIVIDUAL_ALIAS").Select(a => (string?)a.Element("ALIAS_NAME")).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a!).ToList();
            var dobs = ind.Elements("INDIVIDUAL_DATE_OF_BIRTH")
                .Select(d => (string?)d.Element("DATE") ?? (string?)d.Element("YEAR"))
                .Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d!).ToList();
            var countries = ind.Elements("NATIONALITY").Select(n => (string?)n.Element("VALUE")).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!).ToList();
            var programs = new[] { (string?)ind.Element("UN_LIST_TYPE") }.Where(p => p is not null).Select(p => p!).ToList();
            yield return new SanctionedEntity($"UN-{id}", ListName, SanctionedEntityType.Person, name, aliases, dobs, countries, programs,
                (string?)ind.Element("COMMENTS1"), new Uri("https://www.un.org/securitycouncil/content/un-sc-consolidated-list"));
        }
        foreach (var ent in doc.Descendants("ENTITY"))
        {
            var name = (string?)ent.Element("FIRST_NAME");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var id = (string?)ent.Element("DATAID") ?? (string?)ent.Element("REFERENCE_NUMBER") ?? Guid.NewGuid().ToString("N");
            var aliases = ent.Elements("ENTITY_ALIAS").Select(a => (string?)a.Element("ALIAS_NAME")).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a!).ToList();
            var countries = ent.Elements("ENTITY_ADDRESS").Select(a => (string?)a.Element("COUNTRY")).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!).Distinct().ToList();
            var programs = new[] { (string?)ent.Element("UN_LIST_TYPE") }.Where(p => p is not null).Select(p => p!).ToList();
            yield return new SanctionedEntity($"UN-{id}", ListName, SanctionedEntityType.Organization, name, aliases, Array.Empty<string>(), countries, programs,
                (string?)ent.Element("COMMENTS1"), new Uri("https://www.un.org/securitycouncil/content/un-sc-consolidated-list"));
        }
    }
}
