using System.Reflection;
using System.Text.Json;
using MerchantIntelligence.MccValidation.Taxonomy;

namespace MerchantIntelligence.Underwriting.Benchmarks;

public sealed record IndustryBenchmark(
    string Source,
    double RevenuePerEmployeeP10,
    double RevenuePerEmployeeP50,
    double RevenuePerEmployeeP90,
    double TicketP10,
    double TicketP90,
    double ChargebackRate,
    int DeliveryDays,
    int MaxEmployeesPerLocation,
    int MaxEmployees);

/// <summary>Embedded per-category / per-MCC SMB benchmarks used for plausibility and exposure maths.</summary>
public sealed class IndustryBenchmarks
{
    private sealed record Raw(double[] RevenuePerEmployee, double[] Ticket, double ChargebackRate, int DeliveryDays,
        int? MaxEmployeesPerLocation = null, int? MaxEmployees = null);

    private const int DefaultMaxEmployeesPerLocation = 300;
    private const int DefaultMaxEmployees = 5000;
    private sealed record File(Dictionary<string, Raw> Categories, Dictionary<string, Raw> MccOverrides);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly Dictionary<string, Raw> _categories;
    private readonly Dictionary<int, Raw> _mcc;
    private readonly MccCatalog _catalog;

    private IndustryBenchmarks(File file, MccCatalog catalog)
    {
        _categories = file.Categories;
        _mcc = file.MccOverrides.ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value);
        _catalog = catalog;
    }

    public static IndustryBenchmarks Default { get; } = LoadEmbedded(MccCatalog.Default);

    public IndustryBenchmark Resolve(int? mcc)
    {
        var category = mcc is int m2 ? _catalog.Find(m2)?.Category : null;
        var c = category is not null && _categories.TryGetValue(category, out var found) ? found : null;
        if (mcc is int m && _mcc.TryGetValue(m, out var o)) return Map($"MCC {m}", o, c);
        if (c is not null) return Map($"Category '{category}'", c, null);
        return Map("All industries", new Raw(new[] { 60000.0, 150000, 500000 }, new[] { 15.0, 1500 }, 0.005, 7), null);
    }

    private static IndustryBenchmark Map(string source, Raw r, Raw? categoryFallback) => new(source,
        r.RevenuePerEmployee[0], r.RevenuePerEmployee[1], r.RevenuePerEmployee[2],
        r.Ticket[0], r.Ticket[1], r.ChargebackRate, r.DeliveryDays,
        r.MaxEmployeesPerLocation ?? categoryFallback?.MaxEmployeesPerLocation ?? DefaultMaxEmployeesPerLocation,
        r.MaxEmployees ?? categoryFallback?.MaxEmployees ?? DefaultMaxEmployees);

    private static IndustryBenchmarks LoadEmbedded(MccCatalog catalog)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("industry-benchmarks.json", StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)!;
        var file = JsonSerializer.Deserialize<File>(stream, JsonOptions)
                   ?? throw new InvalidOperationException("industry-benchmarks.json is empty.");
        return new IndustryBenchmarks(file, catalog);
    }
}
