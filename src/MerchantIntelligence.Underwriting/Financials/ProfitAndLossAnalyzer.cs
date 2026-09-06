using System.Globalization;
using System.Text.RegularExpressions;
using MerchantIntelligence.MccValidation.Taxonomy;
using UglyToad.PdfPig;

namespace MerchantIntelligence.Underwriting.Financials;

public sealed record ProfitAndLoss(
    decimal? Revenue,
    decimal? CostOfGoodsSold,
    decimal? GrossProfit,
    decimal? OperatingExpenses,
    decimal? OperatingIncome,
    decimal? InterestExpense,
    decimal? Depreciation,
    decimal? NetIncome,
    decimal? TotalAssets,
    decimal? CurrentAssets,
    decimal? Cash,
    decimal? TotalLiabilities,
    decimal? CurrentLiabilities,
    decimal? TotalDebt,
    decimal? Equity,
    IReadOnlyDictionary<string, decimal> RawLines);

public sealed record FinancialRatio(string Name, double? Value, string Benchmark, string Assessment);

public sealed record FinancialStatementAnalysis(
    ProfitAndLoss Statement,
    IReadOnlyList<FinancialRatio> Ratios,
    IReadOnlyList<CashFlowFlag> Flags,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Extracts line items from a P&amp;L / balance sheet supplied as CSV ("label,amount" rows), plain text
/// or text-based PDF, then computes standard underwriting ratios (gross margin, net margin, DSCR
/// proxy, current ratio, leverage). Label matching is synonym-based and case-insensitive.
/// </summary>
public static class ProfitAndLossAnalyzer
{
    private static readonly (string Key, Regex Pattern)[] LineItems =
    {
        ("Revenue", new Regex(@"^(total\s+)?(net\s+)?(revenue|sales|income from operations|turnover|gross receipts)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("CostOfGoodsSold", new Regex(@"^(total\s+)?(cost of (goods|sales|revenue)|cogs|direct costs)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("GrossProfit", new Regex(@"^gross (profit|margin)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("OperatingExpenses", new Regex(@"^(total\s+)?(operating|opex|sg&a|selling, general|general and administrative|overheads?)\b.*(expense|cost|opex|sg&a|overhead)?", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("OperatingIncome", new Regex(@"^(operating (income|profit)|ebit\b|income from operations|profit from operations)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("InterestExpense", new Regex(@"^interest (expense|paid|charges?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Depreciation", new Regex(@"^depreciation|^amortisation|^amortization|^d&a\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("NetIncome", new Regex(@"^(net (income|profit|earnings|loss)|profit (after tax|for the (year|period))|bottom line)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("TotalAssets", new Regex(@"^total assets\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("CurrentAssets", new Regex(@"^(total\s+)?current assets\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Cash", new Regex(@"^cash( and (cash )?equivalents)?\b|^bank balances?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("TotalLiabilities", new Regex(@"^total liabilities\b(?! and)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("CurrentLiabilities", new Regex(@"^(total\s+)?current liabilities\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("TotalDebt", new Regex(@"^(total\s+)?(debt|borrowings|loans payable|notes payable|long[- ]term debt)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Equity", new Regex(@"^(total\s+)?(shareholders'?|stockholders'?|owners'?|members'?)?\s*equity\b|^net assets\b|^retained earnings and equity", RegexOptions.IgnoreCase | RegexOptions.Compiled))
    };

    private static readonly Regex TrailingAmount = new(@"(?<label>.+?)[\s,;:\t]+(?<amount>\(?-?[$£€]?\s?\d[\d,]*(\.\d{1,2})?\)?)\s*$", RegexOptions.Compiled);

    public static FinancialStatementAnalysis Analyze(Stream stream, string fileName, decimal? declaredAnnualCardVolume = null)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        string text;
        if (ext == ".pdf")
        {
            using var pdf = PdfDocument.Open(stream);
            text = string.Join('\n', pdf.GetPages().Select(p => string.Join('\n', p.GetWords()
                .GroupBy(w => Math.Round(w.BoundingBox.Bottom / 3.0)).OrderByDescending(g => g.Key)
                .Select(g => string.Join(' ', g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text))))));
        }
        else
        {
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        return AnalyzeText(text, declaredAnnualCardVolume);
    }

    public static FinancialStatementAnalysis AnalyzeText(string text, decimal? declaredAnnualCardVolume = null)
    {
        var raw = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var mapped = new Dictionary<string, decimal>();
        var warnings = new List<string>();

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().Trim('"');
            if (line.Length == 0) continue;
            var m = TrailingAmount.Match(line.Replace("\",\"", "\t").Replace('"', ' '));
            if (!m.Success) continue;
            var label = m.Groups["label"].Value.Trim().Trim(',', ';', ':', '"', ' ');
            var amount = BankStatementParser.TryParseAmount(m.Groups["amount"].Value);
            if (amount is null || label.Length < 3 || Regex.IsMatch(label, @"^\d")) continue;
            raw[label] = amount.Value;
            foreach (var (key, pattern) in LineItems)
            {
                if (mapped.ContainsKey(key) || !pattern.IsMatch(label)) continue;
                mapped[key] = amount.Value;
                break;
            }
        }
        if (mapped.Count == 0) warnings.Add("No recognisable P&L or balance-sheet line items found.");

        decimal? Get(string k) => mapped.TryGetValue(k, out var v) ? v : null;
        var revenue = Get("Revenue");
        var cogs = Get("CostOfGoodsSold");
        var gross = Get("GrossProfit") ?? (revenue is not null && cogs is not null ? revenue - Math.Abs(cogs.Value) : null);
        var opex = Get("OperatingExpenses");
        var opIncome = Get("OperatingIncome") ?? (gross is not null && opex is not null ? gross - Math.Abs(opex.Value) : null);
        var interest = Get("InterestExpense");
        var da = Get("Depreciation");
        var net = Get("NetIncome");
        var statement = new ProfitAndLoss(revenue, cogs, gross, opex, opIncome, interest, da, net,
            Get("TotalAssets"), Get("CurrentAssets"), Get("Cash"), Get("TotalLiabilities"), Get("CurrentLiabilities"), Get("TotalDebt"), Get("Equity"), raw);

        var ratios = new List<FinancialRatio>();
        var flags = new List<CashFlowFlag>();

        double? Ratio(decimal? a, decimal? b) => a is not null && b is not null && b != 0 ? Math.Round((double)(a.Value / b.Value), 3) : null;

        var grossMargin = Ratio(gross, revenue);
        ratios.Add(new FinancialRatio("Gross margin", grossMargin, "> 0.25", grossMargin is null ? "n/a" : grossMargin < 0.1 ? "Very thin" : grossMargin < 0.25 ? "Thin" : "Healthy"));
        var netMargin = Ratio(net, revenue);
        ratios.Add(new FinancialRatio("Net margin", netMargin, "> 0.03", netMargin is null ? "n/a" : netMargin < 0 ? "Loss-making" : netMargin < 0.03 ? "Marginal" : "Healthy"));
        var ebitda = opIncome is not null ? opIncome + Math.Abs(da ?? 0) : null;
        var dscr = Ratio(ebitda, interest is not null && interest != 0 ? Math.Abs(interest.Value) : null);
        ratios.Add(new FinancialRatio("EBITDA / interest (coverage)", dscr, "> 3", dscr is null ? "n/a" : dscr < 1.25 ? "Cannot service debt" : dscr < 3 ? "Tight" : "Comfortable"));
        var current = Ratio(statement.CurrentAssets, statement.CurrentLiabilities);
        ratios.Add(new FinancialRatio("Current ratio", current, "> 1.2", current is null ? "n/a" : current < 1 ? "Illiquid" : current < 1.2 ? "Tight" : "Adequate"));
        var leverage = Ratio(statement.TotalLiabilities ?? statement.TotalDebt, statement.Equity);
        ratios.Add(new FinancialRatio("Liabilities / equity", leverage, "< 2.5", leverage is null ? "n/a" : statement.Equity <= 0 ? "Negative equity" : leverage > 4 ? "Highly leveraged" : leverage > 2.5 ? "Leveraged" : "Conservative"));
        var cashToRevenue = Ratio(statement.Cash, revenue is not null ? revenue / 12 : null);
        ratios.Add(new FinancialRatio("Cash / monthly revenue", cashToRevenue, "> 1", cashToRevenue is null ? "n/a" : cashToRevenue < 0.5 ? "Under two weeks of cash" : cashToRevenue < 1 ? "Under a month of cash" : "Adequate"));

        if (netMargin is < 0) flags.Add(new CashFlowFlag("LOSS_MAKING", $"Net margin {netMargin:P1}: the business is loss-making.", RiskTier.High));
        if (grossMargin is < 0.1) flags.Add(new CashFlowFlag("THIN_GROSS_MARGIN", $"Gross margin {grossMargin:P1}; little buffer against chargebacks or refunds.", RiskTier.Medium));
        if (dscr is < 1.25) flags.Add(new CashFlowFlag("WEAK_DEBT_COVERAGE", $"EBITDA covers interest only {dscr:F2}x.", RiskTier.High));
        if (current is < 1) flags.Add(new CashFlowFlag("ILLIQUID", $"Current ratio {current:F2}: current liabilities exceed current assets.", RiskTier.High));
        if (statement.Equity is <= 0) flags.Add(new CashFlowFlag("NEGATIVE_EQUITY", "Liabilities exceed assets.", RiskTier.High));
        else if (leverage is > 4) flags.Add(new CashFlowFlag("HIGH_LEVERAGE", $"Liabilities are {leverage:F1}x equity.", RiskTier.Medium));
        if (declaredAnnualCardVolume is decimal declared && revenue is decimal rev && rev > 0 && declared > rev * 1.2m)
            flags.Add(new CashFlowFlag("CARD_VOLUME_EXCEEDS_REVENUE", $"Declared card volume {declared:N0} exceeds reported revenue {rev:N0} by {(declared / rev - 1):P0}.", RiskTier.High));

        return new FinancialStatementAnalysis(statement, ratios, flags, warnings);
    }
}
