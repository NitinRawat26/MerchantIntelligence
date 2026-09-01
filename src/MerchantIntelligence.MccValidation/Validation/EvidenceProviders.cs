using System.Text.RegularExpressions;
using MerchantIntelligence.MccValidation.Classification;
using MerchantIntelligence.MccValidation.Edgar;
using MerchantIntelligence.MccValidation.Taxonomy;
using MerchantIntelligence.MccValidation.Web;

namespace MerchantIntelligence.MccValidation.Validation;

public sealed record ValidationContext(int DeclaredMcc, Uri WebsiteUrl, WebsiteContent Website);

public interface IMccEvidenceProvider
{
    string Name { get; }

    /// <summary>Relative weight in the aggregate vote.</summary>
    double Weight { get; }

    Task<ProviderEvidence> EvaluateAsync(ValidationContext context, CancellationToken ct);
}

/// <summary>Deterministic keyword match of website text against the MCC catalog.</summary>
public sealed class KeywordTaxonomyProvider : IMccEvidenceProvider
{
    private readonly MccCatalog _catalog;

    public KeywordTaxonomyProvider(MccCatalog catalog) => _catalog = catalog;

    public string Name => "Website keyword taxonomy";
    public double Weight => 1.0;

    public Task<ProviderEvidence> EvaluateAsync(ValidationContext context, CancellationToken ct)
    {
        if (context.Website.IsEmpty)
            return Task.FromResult(ProviderEvidence.Failed(Name, "No website text available."));

        var text = context.Website.CombinedText.ToLowerInvariant();
        var hits = new List<(MccEntry Entry, double Score, List<string> Matched)>();

        foreach (var entry in _catalog.Entries)
        {
            var matched = new List<string>();
            double score = 0;
            foreach (var keyword in entry.Keywords)
            {
                var count = Regex.Matches(text, $@"\b{Regex.Escape(keyword.ToLowerInvariant())}\b").Count;
                if (count == 0) continue;
                matched.Add(keyword);
                // Diminishing returns per keyword; multi-word phrases are more specific.
                score += Math.Log(1 + count) * (keyword.Contains(' ') ? 1.5 : 1.0);
            }
            if (matched.Count > 0) hits.Add((entry, score, matched));
        }

        if (hits.Count == 0)
            return Task.FromResult(new ProviderEvidence(Name, true, Array.Empty<MccCandidate>(), new[] { "No catalog keywords found on the site." }));

        var total = hits.Sum(h => h.Score);
        var candidates = hits.OrderByDescending(h => h.Score).Take(5)
            .Select(h => new MccCandidate(h.Entry.Mcc, h.Entry.Description, h.Score / total))
            .ToList();
        var highlights = hits.OrderByDescending(h => h.Score).Take(3)
            .Select(h => $"{h.Entry.Mcc} {h.Entry.Description}: {string.Join(", ", h.Matched.Take(6))}")
            .ToList();

        return Task.FromResult(new ProviderEvidence(Name, true, candidates, highlights));
    }
}

/// <summary>ML.NET text classifier trained on EDGAR 10-K business descriptions + company sites.</summary>
public sealed class TextClassifierProvider : IMccEvidenceProvider
{
    private readonly MccTextClassifier _classifier;
    private readonly MccCatalog _catalog;

    public TextClassifierProvider(MccTextClassifier classifier, MccCatalog catalog)
    {
        _classifier = classifier;
        _catalog = catalog;
    }

    public string Name => "ML text classifier (EDGAR-trained)";
    public double Weight => 1.5;

    public Task<ProviderEvidence> EvaluateAsync(ValidationContext context, CancellationToken ct)
    {
        if (context.Website.IsEmpty)
            return Task.FromResult(ProviderEvidence.Failed(Name, "No website text available."));

        var prediction = _classifier.Predict(context.Website.CombinedText);
        var candidates = prediction.Ranked
            .Select(r => new MccCandidate(r.Mcc, _catalog.Describe(r.Mcc), r.Probability))
            .ToList();
        var highlights = new[]
        {
            $"Top prediction {prediction.Top.Mcc} {_catalog.Describe(prediction.Top.Mcc)} at {prediction.Top.Probability:P0}",
            $"Declared MCC probability {prediction.ProbabilityOf(context.DeclaredMcc):P0} across {_classifier.ClassCount} classes"
        };
        return Task.FromResult(new ProviderEvidence(Name, true, candidates, highlights));
    }
}

/// <summary>schema.org JSON-LD @type hints (Restaurant, Hotel, Store, ...).</summary>
public sealed class StructuredDataProvider : IMccEvidenceProvider
{
    private static readonly Dictionary<string, int> TypeToMcc = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Restaurant"] = 5812, ["FoodEstablishment"] = 5812, ["CafeOrCoffeeShop"] = 5812, ["Bakery"] = 5462,
        ["BarOrPub"] = 5813, ["FastFoodRestaurant"] = 5814,
        ["Hotel"] = 7011, ["LodgingBusiness"] = 7011, ["Resort"] = 7011,
        ["GroceryStore"] = 5411, ["ClothingStore"] = 5651, ["ShoeStore"] = 5661, ["JewelryStore"] = 5944,
        ["ElectronicsStore"] = 5732, ["FurnitureStore"] = 5712, ["BookStore"] = 5942, ["PetStore"] = 5995,
        ["SportingGoodsStore"] = 5941, ["ToyStore"] = 5945, ["Florist"] = 5992, ["LiquorStore"] = 5921,
        ["Pharmacy"] = 5912, ["DepartmentStore"] = 5311, ["AutoDealer"] = 5511, ["AutoRepair"] = 7538,
        ["GasStation"] = 5541, ["HardwareStore"] = 5072, ["Store"] = 5999, ["OnlineStore"] = 5964,
        ["Physician"] = 8011, ["MedicalClinic"] = 8011, ["Dentist"] = 8021, ["Hospital"] = 8062,
        ["Optician"] = 8043, ["LegalService"] = 8111, ["Attorney"] = 8111, ["AccountingService"] = 8931,
        ["BankOrCreditUnion"] = 6011, ["FinancialService"] = 6012, ["InsuranceAgency"] = 6300,
        ["RealEstateAgent"] = 6513, ["TravelAgency"] = 4722, ["Airline"] = 4511,
        ["BeautySalon"] = 7230, ["HairSalon"] = 7230, ["DaySpa"] = 7298, ["HealthClub"] = 7997,
        ["Casino"] = 7995, ["MovieTheater"] = 7832, ["AmusementPark"] = 7991, ["Museum"] = 7991,
        ["School"] = 8299, ["CollegeOrUniversity"] = 8220, ["ChildCare"] = 8351, ["NGO"] = 8398,
        ["SoftwareApplication"] = 7372, ["WebApplication"] = 7372, ["GeneralContractor"] = 1520,
        ["Plumber"] = 1711, ["Electrician"] = 1731, ["RoofingContractor"] = 1799, ["MovingCompany"] = 4214,
        ["HomeAndConstructionBusiness"] = 1520, ["AutoRental"] = 7512, ["TaxiService"] = 4121
    };

    private readonly MccCatalog _catalog;

    public StructuredDataProvider(MccCatalog catalog) => _catalog = catalog;

    public string Name => "schema.org structured data";
    public double Weight => 0.8;

    public Task<ProviderEvidence> EvaluateAsync(ValidationContext context, CancellationToken ct)
    {
        var types = context.Website.SchemaOrgTypes;
        if (types.Count == 0)
            return Task.FromResult(new ProviderEvidence(Name, true, Array.Empty<MccCandidate>(), new[] { "No JSON-LD found on the site." }));

        var mapped = types.Where(TypeToMcc.ContainsKey).Select(t => (Type: t, Mcc: TypeToMcc[t])).ToList();
        var highlights = new List<string> { $"Declared types: {string.Join(", ", types.Take(8))}" };
        if (mapped.Count == 0)
            return Task.FromResult(new ProviderEvidence(Name, true, Array.Empty<MccCandidate>(), highlights));

        var candidates = mapped.GroupBy(m => m.Mcc)
            .Select(g => new MccCandidate(g.Key, _catalog.Describe(g.Key), (double)g.Count() / mapped.Count))
            .OrderByDescending(c => c.Score)
            .ToList();
        highlights.AddRange(mapped.Select(m => $"{m.Type} → {m.Mcc} {_catalog.Describe(m.Mcc)}"));
        return Task.FromResult(new ProviderEvidence(Name, true, candidates, highlights));
    }
}

/// <summary>
/// If the merchant is an SEC filer, look up its SIC code and map it through the crosswalk.
/// Matches on website domain against the cached EDGAR company index.
/// </summary>
public sealed class EdgarSicProvider : IMccEvidenceProvider
{
    private readonly IEdgarDomainIndex _index;
    private readonly MccCatalog _catalog;

    public EdgarSicProvider(IEdgarDomainIndex index, MccCatalog catalog)
    {
        _index = index;
        _catalog = catalog;
    }

    public string Name => "SEC EDGAR filer (SIC crosswalk)";
    public double Weight => 1.2;

    public async Task<ProviderEvidence> EvaluateAsync(ValidationContext context, CancellationToken ct)
    {
        var host = context.WebsiteUrl.Host;
        var filer = await _index.FindByDomainAsync(host, ct);
        if (filer is null)
            return new ProviderEvidence(Name, true, Array.Empty<MccCandidate>(), new[] { $"{host} is not a known SEC filer domain." });

        if (filer.Sic is null || _catalog.MapSicToMcc(filer.Sic.Value) is not int mcc)
            return new ProviderEvidence(Name, true, Array.Empty<MccCandidate>(), new[] { $"Filer {filer.Name} (CIK {filer.Cik}) has no mappable SIC." });

        return new ProviderEvidence(Name, true,
            new[] { new MccCandidate(mcc, _catalog.Describe(mcc), 0.9) },
            new[] { $"SEC filer {filer.Name} (CIK {filer.Cik}), SIC {filer.Sic} {filer.SicDescription} → MCC {mcc}" });
    }
}

public sealed record EdgarFilerSummary(int Cik, string Name, int? Sic, string SicDescription, string Domain);

public interface IEdgarDomainIndex
{
    Task<EdgarFilerSummary?> FindByDomainAsync(string host, CancellationToken ct);
}

/// <summary>Domain index backed by a JSON file produced by the data pipeline.</summary>
public sealed class FileEdgarDomainIndex : IEdgarDomainIndex
{
    private readonly Lazy<Dictionary<string, EdgarFilerSummary>> _entries;

    public FileEdgarDomainIndex(string? path)
    {
        _entries = new Lazy<Dictionary<string, EdgarFilerSummary>>(() =>
        {
            if (path is null || !File.Exists(path)) return new Dictionary<string, EdgarFilerSummary>(StringComparer.OrdinalIgnoreCase);
            var list = System.Text.Json.JsonSerializer.Deserialize<List<EdgarFilerSummary>>(File.ReadAllText(path),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<EdgarFilerSummary>();
            return list.GroupBy(e => NormalizeHost(e.Domain), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        });
    }

    public Task<EdgarFilerSummary?> FindByDomainAsync(string host, CancellationToken ct) =>
        Task.FromResult(_entries.Value.GetValueOrDefault(NormalizeHost(host)));

    public static string NormalizeHost(string host)
    {
        host = host.Trim().ToLowerInvariant();
        if (host.Contains("://") && Uri.TryCreate(host, UriKind.Absolute, out var uri)) host = uri.Host;
        return host.StartsWith("www.") ? host[4..] : host;
    }
}
