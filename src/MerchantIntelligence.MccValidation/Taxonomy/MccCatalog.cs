using System.Reflection;
using System.Text.Json;

namespace MerchantIntelligence.MccValidation.Taxonomy;

public enum RiskTier
{
    Low,
    Medium,
    High
}

public sealed record MccEntry(
    int Mcc,
    string Description,
    string Category,
    RiskTier RiskTier,
    IReadOnlyList<string> Keywords);

public sealed record SicToMccEntry(int Sic, string SicDescription, int Mcc);

/// <summary>
/// Curated MCC catalog and SIC→MCC crosswalk, loaded from embedded JSON resources.
/// </summary>
public sealed class MccCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static readonly Lazy<MccCatalog> Instance = new(LoadEmbedded);

    private readonly Dictionary<int, MccEntry> _byMcc;
    private readonly Dictionary<int, SicToMccEntry> _bySic;

    public MccCatalog(IEnumerable<MccEntry> entries, IEnumerable<SicToMccEntry> crosswalk)
    {
        _byMcc = entries.ToDictionary(e => e.Mcc);
        _bySic = crosswalk.ToDictionary(e => e.Sic);
    }

    public static MccCatalog Default => Instance.Value;

    public IReadOnlyCollection<MccEntry> Entries => _byMcc.Values;

    public IReadOnlyCollection<SicToMccEntry> Crosswalk => _bySic.Values;

    public MccEntry? Find(int mcc) => _byMcc.GetValueOrDefault(mcc);

    public bool Contains(int mcc) => _byMcc.ContainsKey(mcc);

    public SicToMccEntry? FindBySic(int sic) => _bySic.GetValueOrDefault(sic);

    public int? MapSicToMcc(int sic) => _bySic.TryGetValue(sic, out var e) ? e.Mcc : null;

    public string Describe(int mcc) => Find(mcc)?.Description ?? $"MCC {mcc}";

    private static MccCatalog LoadEmbedded()
    {
        var entries = ReadResource<List<MccEntry>>("mcc-catalog.json");
        var crosswalk = ReadResource<List<SicToMccEntry>>("sic-to-mcc.json");
        return new MccCatalog(entries, crosswalk);
    }

    private static T ReadResource<T>(string fileName)
    {
        var assembly = typeof(MccCatalog).GetTypeInfo().Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource {fileName} not found.");
        return JsonSerializer.Deserialize<T>(stream, JsonOptions)
            ?? throw new InvalidOperationException($"Embedded resource {fileName} is empty.");
    }
}
