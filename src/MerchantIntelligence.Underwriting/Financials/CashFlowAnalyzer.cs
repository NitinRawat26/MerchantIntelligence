using System.Text.RegularExpressions;
using MerchantIntelligence.MccValidation.Taxonomy;

namespace MerchantIntelligence.Underwriting.Financials;

public sealed record MonthlySummary(string Month, decimal Inflows, decimal Outflows, decimal Net, decimal CardProcessorDeposits, int TransactionCount, decimal? EndingBalance);

public sealed record CashFlowFlag(string Code, string Message, RiskTier Severity);

public sealed record CashFlowAnalysis(
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    int MonthsCovered,
    int TransactionCount,
    decimal TotalInflows,
    decimal TotalOutflows,
    decimal AverageMonthlyInflows,
    decimal AverageMonthlyOutflows,
    decimal AverageMonthlyNet,
    decimal AverageMonthlyCardDeposits,
    // Card deposits net of typical processor fees, annualised: comparable with declared card volume.
    decimal ImpliedAnnualCardVolume,
    decimal? AverageDailyBalance,
    decimal? MinimumBalance,
    int NegativeBalanceDays,
    int NsfOrOverdraftCount,
    int ReturnedItemCount,
    decimal LoanRepayments,
    decimal Payroll,
    decimal OwnerDraws,
    decimal LargestSingleDeposit,
    // Coefficient of variation of monthly inflows (0 = perfectly steady).
    double InflowVolatility,
    // Share of inflows arriving in the strongest 3 months; > 0.5 indicates strong seasonality.
    double SeasonalityIndex,
    IReadOnlyList<string> DetectedProcessors,
    IReadOnlyList<MonthlySummary> Monthly,
    IReadOnlyList<CashFlowFlag> Flags,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Derives underwriting ratios from parsed bank transactions: monthly cash flow, card-processor
/// deposits (Stripe, Square, PayPal, acquirer settlements, …), NSF/overdraft events, balances,
/// debt service, volatility and seasonality.
/// </summary>
public static class CashFlowAnalyzer
{
    private static readonly (string Name, Regex Pattern)[] Processors =
    {
        ("Stripe", new Regex(@"\bstripe\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Square", new Regex(@"\bsquare\b|\bsq \*|\bsquare inc", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("PayPal", new Regex(@"\bpaypal\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Shopify Payments", new Regex(@"\bshopify\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Adyen", new Regex(@"\badyen\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Worldpay", new Regex(@"\bworldpay\b|\bvantiv\b|\bfis merchant", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Fiserv / First Data", new Regex(@"\bfiserv\b|\bfirst data\b|\bclover\b|\bcardconnect\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Elavon", new Regex(@"\belavon\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Global Payments / TSYS", new Regex(@"\bglobal pay|\btsys\b|\bheartland\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Chase Paymentech", new Regex(@"\bpaymentech\b|\bchase merchant", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("American Express", new Regex(@"\bamerican express\b|\bamex\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Toast", new Regex(@"\btoast\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Authorize.net", new Regex(@"authorize\.?net", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("Generic merchant settlement", new Regex(@"\bmerchant (services|settlement|deposit|bankcard)|\bbankcard\b|\bcard settlement|\bmtot dep|\bcredit card deposit", RegexOptions.IgnoreCase | RegexOptions.Compiled))
    };

    private static readonly Regex NsfRegex = new(@"\bnsf\b|non[- ]?sufficient|insufficient funds|overdraft (fee|charge|item)|\bod fee|returned item fee|unpaid item", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReturnedRegex = new(@"\breturned\b|\breversal\b|\bchargeback\b|\bbounced\b|\bunpaid\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LoanRegex = new(@"\bloan\b|\blending\b|\bkabbage\b|\bondeck\b|\bfundbox\b|\bsba\b|\bmca\b|merchant cash|\bcapital (advance|funding)\b|\bfinancing\b|\bpayoff\b|\blease pay", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PayrollRegex = new(@"\bpayroll\b|\bgusto\b|\badp\b|\bpaychex\b|\bintuit payroll|\bwages\b|\bsalary\b|\bsalaries\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OwnerDrawRegex = new(@"owner('s)? draw|\bdistribution\b|\bdividend\b|transfer to (savings|personal)|\bzelle\b|\bvenmo\b|\bcash app\b|\bcashapp\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Typical blended processor fee: gross card volume ≈ net deposits / (1 - fee).
    private const decimal AssumedProcessorFee = 0.029m;

    public static CashFlowAnalysis Analyze(ParsedStatement statement)
    {
        var txns = statement.Transactions;
        var warnings = new List<string>(statement.Warnings);
        var flags = new List<CashFlowFlag>();
        if (txns.Count == 0)
            throw new InvalidDataException("No transactions to analyse. " + string.Join(' ', warnings));

        var start = txns.Min(t => t.Date);
        var end = txns.Max(t => t.Date);
        var months = txns.GroupBy(t => new DateOnly(t.Date.Year, t.Date.Month, 1)).OrderBy(g => g.Key).ToList();
        var monthsCovered = months.Count;
        if (monthsCovered < 3) flags.Add(new CashFlowFlag("SHORT_STATEMENT_HISTORY", $"Only {monthsCovered} month(s) of statements supplied; underwriters typically require 3-6.", RiskTier.Medium));

        var processorsSeen = new HashSet<string>();
        var monthly = new List<MonthlySummary>();
        foreach (var g in months)
        {
            var inflows = g.Where(t => t.Amount > 0).Sum(t => t.Amount);
            var outflows = -g.Where(t => t.Amount < 0).Sum(t => t.Amount);
            var card = 0m;
            foreach (var t in g.Where(t => t.Amount > 0))
            {
                var p = Processors.FirstOrDefault(p => p.Pattern.IsMatch(t.Description));
                if (p.Name is null) continue;
                processorsSeen.Add(p.Name);
                card += t.Amount;
            }
            var ending = g.OrderBy(t => t.Date).LastOrDefault(t => t.Balance.HasValue)?.Balance;
            monthly.Add(new MonthlySummary(g.Key.ToString("yyyy-MM"), inflows, outflows, inflows - outflows, card, g.Count(), ending));
        }

        var totalIn = monthly.Sum(m => m.Inflows);
        var totalOut = monthly.Sum(m => m.Outflows);
        var avgIn = totalIn / monthsCovered;
        var avgOut = totalOut / monthsCovered;
        var avgCard = monthly.Sum(m => m.CardProcessorDeposits) / monthsCovered;
        var impliedAnnualCard = Math.Round(avgCard * 12 / (1 - AssumedProcessorFee), 0);

        var nsf = txns.Count(t => NsfRegex.IsMatch(t.Description));
        var returned = txns.Count(t => ReturnedRegex.IsMatch(t.Description) && !NsfRegex.IsMatch(t.Description));
        var loans = -txns.Where(t => t.Amount < 0 && LoanRegex.IsMatch(t.Description)).Sum(t => t.Amount);
        var payroll = -txns.Where(t => t.Amount < 0 && PayrollRegex.IsMatch(t.Description)).Sum(t => t.Amount);
        var draws = -txns.Where(t => t.Amount < 0 && OwnerDrawRegex.IsMatch(t.Description)).Sum(t => t.Amount);
        var largestDeposit = txns.Where(t => t.Amount > 0).Select(t => t.Amount).DefaultIfEmpty(0).Max();

        decimal? avgBalance = null, minBalance = null;
        var negativeDays = 0;
        var withBalance = txns.Where(t => t.Balance.HasValue).ToList();
        if (withBalance.Count > 0)
        {
            var daily = withBalance.GroupBy(t => t.Date).Select(g => g.OrderBy(t => t.Amount).Last().Balance!.Value).ToList();
            avgBalance = Math.Round(daily.Average(), 2);
            minBalance = daily.Min();
            negativeDays = daily.Count(b => b < 0);
        }
        else warnings.Add("No running balance column; balance metrics unavailable.");

        var inflowSeries = monthly.Select(m => (double)m.Inflows).ToList();
        var volatility = inflowSeries.Count > 1 && inflowSeries.Average() > 0
            ? Math.Round(Math.Sqrt(inflowSeries.Sum(x => Math.Pow(x - inflowSeries.Average(), 2)) / (inflowSeries.Count - 1)) / inflowSeries.Average(), 3)
            : 0;
        var seasonality = totalIn > 0 && monthsCovered >= 6
            ? Math.Round((double)(inflowSeries.OrderByDescending(x => x).Take(3).Sum() / (double)totalIn), 3)
            : 0;

        // Flags.
        if (nsf >= 3) flags.Add(new CashFlowFlag("FREQUENT_NSF", $"{nsf} NSF / overdraft fee events in {monthsCovered} month(s).", RiskTier.High));
        else if (nsf > 0) flags.Add(new CashFlowFlag("NSF_PRESENT", $"{nsf} NSF / overdraft fee event(s).", RiskTier.Medium));
        if (negativeDays > 0) flags.Add(new CashFlowFlag("NEGATIVE_BALANCE_DAYS", $"Account balance was negative on {negativeDays} day(s).", negativeDays >= 5 ? RiskTier.High : RiskTier.Medium));
        if (avgIn > 0 && avgOut > avgIn * 1.1m) flags.Add(new CashFlowFlag("NEGATIVE_CASH_FLOW", $"Average monthly outflows ({avgOut:N0}) exceed inflows ({avgIn:N0}) by more than 10%.", RiskTier.High));
        if (avgIn > 0 && loans / monthsCovered > avgIn * 0.2m) flags.Add(new CashFlowFlag("HIGH_DEBT_SERVICE", $"Loan / advance repayments are {loans / monthsCovered / avgIn:P0} of monthly inflows.", RiskTier.High));
        if (avgBalance is decimal ab && avgOut > 0 && ab < avgOut * 0.25m) flags.Add(new CashFlowFlag("THIN_LIQUIDITY", $"Average balance ({ab:N0}) covers less than a week of outflows.", RiskTier.Medium));
        if (volatility > 0.6) flags.Add(new CashFlowFlag("VOLATILE_INFLOWS", $"Monthly inflows vary widely (CV {volatility:F2}).", RiskTier.Medium));
        if (seasonality > 0.55) flags.Add(new CashFlowFlag("SEASONAL_BUSINESS", $"Top 3 months carry {seasonality:P0} of inflows; size reserves for the off-season.", RiskTier.Low));
        if (totalIn > 0 && largestDeposit > totalIn * 0.4m) flags.Add(new CashFlowFlag("LUMPY_DEPOSITS", $"A single deposit of {largestDeposit:N0} is {largestDeposit / totalIn:P0} of all inflows (capital injection or one-off?).", RiskTier.Medium));
        if (processorsSeen.Count == 0) flags.Add(new CashFlowFlag("NO_CARD_DEPOSITS", "No card-processor settlements identified; declared card volume cannot be corroborated from statements.", RiskTier.Low));
        else if (processorsSeen.Count >= 3) flags.Add(new CashFlowFlag("MULTIPLE_PROCESSORS", $"Settlements from {processorsSeen.Count} processors ({string.Join(", ", processorsSeen)}); check for load balancing or prior terminations.", RiskTier.Medium));
        if (avgIn > 0 && draws / monthsCovered > avgIn * 0.3m) flags.Add(new CashFlowFlag("HIGH_OWNER_DRAWS", $"Owner draws / P2P transfers are {draws / monthsCovered / avgIn:P0} of monthly inflows.", RiskTier.Medium));

        return new CashFlowAnalysis(start, end, monthsCovered, txns.Count, totalIn, totalOut,
            Math.Round(avgIn, 2), Math.Round(avgOut, 2), Math.Round(avgIn - avgOut, 2), Math.Round(avgCard, 2), impliedAnnualCard,
            avgBalance, minBalance, negativeDays, nsf, returned, loans, payroll, draws, largestDeposit,
            volatility, seasonality, processorsSeen.OrderBy(p => p).ToList(), monthly, flags, warnings);
    }
}
