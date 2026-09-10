using MerchantIntelligence.MccValidation.Taxonomy;
using MerchantIntelligence.Underwriting.Benchmarks;

namespace MerchantIntelligence.Underwriting.Plausibility;

public sealed record VolumeDeclaration(
    decimal AnnualVolume,
    decimal AverageTicket,
    decimal? HighestTicket = null,
    int? MerchantCategoryCode = null,
    int? EmployeeCount = null,
    decimal? YearsInBusiness = null,
    decimal? PriorYearRevenue = null,
    decimal? MonthlyCardVolumeFromStatements = null,
    int? WebsiteProductCount = null,
    bool? HasPhysicalLocation = null,
    int? LocationCount = null);

public sealed record PlausibilityFlag(string Code, string Message, RiskTier Severity);

public sealed record PlausibilityMetric(string Name, string Value, string Benchmark, string Assessment);

public sealed record VolumePlausibilityResult(
    // 0 (implausible) .. 100 (fully consistent with benchmarks and supplied evidence)
    int PlausibilityScore,
    string Verdict,
    IReadOnlyList<PlausibilityMetric> Metrics,
    IReadOnlyList<PlausibilityFlag> Flags,
    string BenchmarkSource);

/// <summary>
/// Cross-checks a merchant's declared processing volume against industry benchmarks and any other
/// evidence supplied (headcount, tenure, prior revenue, bank statements, catalogue size). Designed
/// to surface inflated volume declarations, bust-out setups and load-balancing shells.
/// </summary>
public sealed class VolumePlausibilityAnalyzer
{
    private readonly IndustryBenchmarks _benchmarks;

    public VolumePlausibilityAnalyzer(IndustryBenchmarks benchmarks) => _benchmarks = benchmarks;

    public VolumePlausibilityResult Analyze(VolumeDeclaration d)
    {
        var bm = _benchmarks.Resolve(d.MerchantCategoryCode);
        var metrics = new List<PlausibilityMetric>();
        var flags = new List<PlausibilityFlag>();
        var penalty = 0.0;

        var volume = (double)d.AnnualVolume;
        var ticket = (double)Math.Max(d.AverageTicket, 0.01m);
        var txPerYear = volume / ticket;
        var txPerDay = txPerYear / 365.0;

        metrics.Add(new PlausibilityMetric("Implied transactions / day", txPerDay.ToString("N1"), "-", txPerDay < 0.2 ? "Very low" : txPerDay > 5000 ? "Very high" : "Normal"));
        if (txPerYear < 24)
        {
            flags.Add(new PlausibilityFlag("IMPLAUSIBLY_FEW_TRANSACTIONS", $"Declared volume and ticket imply only {txPerYear:N0} card transactions per year.", RiskTier.Medium));
            penalty += 15;
        }

        // Ticket vs benchmark band.
        var ticketAssessment = ticket < bm.TicketP10 ? "Below industry p10" : ticket > bm.TicketP90 ? "Above industry p90" : "Within p10-p90";
        metrics.Add(new PlausibilityMetric("Average ticket", ticket.ToString("N2"), $"{bm.TicketP10:N0} - {bm.TicketP90:N0}", ticketAssessment));
        if (ticket > bm.TicketP90 * 3)
        {
            flags.Add(new PlausibilityFlag("TICKET_FAR_ABOVE_INDUSTRY", $"Average ticket {ticket:N2} is more than 3x the industry p90 ({bm.TicketP90:N0}).", RiskTier.High));
            penalty += 25;
        }
        else if (ticket > bm.TicketP90)
        {
            flags.Add(new PlausibilityFlag("TICKET_ABOVE_INDUSTRY", $"Average ticket {ticket:N2} exceeds the industry p90 ({bm.TicketP90:N0}).", RiskTier.Medium));
            penalty += 10;
        }

        if (d.HighestTicket is decimal highest && highest > 0)
        {
            var ratio = (double)highest / ticket;
            metrics.Add(new PlausibilityMetric("Highest / average ticket", ratio.ToString("N1") + "x", "< 25x", ratio > 25 ? "Irregular large sales" : "Normal"));
            if (ratio > 50)
            {
                flags.Add(new PlausibilityFlag("EXTREME_TICKET_SPREAD", $"Highest ticket is {ratio:N0}x the average; expect sporadic very large sales or mis-declaration.", RiskTier.Medium));
                penalty += 10;
            }
        }

        // Revenue per employee.
        if (d.EmployeeCount is int employees && employees > 0)
        {
            var rpe = volume / employees;
            var assess = rpe > bm.RevenuePerEmployeeP90 * 2 ? "More than 2x industry p90"
                : rpe > bm.RevenuePerEmployeeP90 ? "Above industry p90"
                : rpe < bm.RevenuePerEmployeeP10 / 20 ? "Less than 1/20th of industry p10"
                : rpe < bm.RevenuePerEmployeeP10 / 4 ? "Less than 1/4 of industry p10"
                : rpe < bm.RevenuePerEmployeeP10 ? "Below industry p10" : "Within p10-p90";
            metrics.Add(new PlausibilityMetric("Card volume / employee", rpe.ToString("N0"), $"{bm.RevenuePerEmployeeP10:N0} - {bm.RevenuePerEmployeeP90:N0}", assess));
            if (rpe > bm.RevenuePerEmployeeP90 * 2)
            {
                flags.Add(new PlausibilityFlag("VOLUME_EXCEEDS_HEADCOUNT_CAPACITY", $"Card volume per employee ({rpe:N0}) is more than double the industry p90 ({bm.RevenuePerEmployeeP90:N0}).", RiskTier.High));
                penalty += 30;
            }
            else if (rpe > bm.RevenuePerEmployeeP90)
            {
                flags.Add(new PlausibilityFlag("VOLUME_HIGH_FOR_HEADCOUNT", $"Card volume per employee ({rpe:N0}) is above the industry p90.", RiskTier.Medium));
                penalty += 12;
            }
            else if (rpe < bm.RevenuePerEmployeeP10 / 20)
            {
                flags.Add(new PlausibilityFlag("HEADCOUNT_IMPLAUSIBLE_FOR_VOLUME", $"{employees:N0} employees on {volume:N0} annual card volume is {rpe:N0} per employee, less than 1/20th of the industry p10 ({bm.RevenuePerEmployeeP10:N0}); the declared headcount cannot be supported by this revenue.", RiskTier.High));
                penalty += 30;
            }
            else if (rpe < bm.RevenuePerEmployeeP10 / 4)
            {
                flags.Add(new PlausibilityFlag("HEADCOUNT_HIGH_FOR_VOLUME", $"Card volume per employee ({rpe:N0}) is less than a quarter of the industry p10 ({bm.RevenuePerEmployeeP10:N0}); headcount looks overstated or card volume understated.", RiskTier.Medium));
                penalty += 12;
            }

            // Absolute headcount ceilings for the industry, scaled by declared locations.
            var locations = d.LocationCount is int l && l > 0 ? l : (int?)null;
            var ceiling = Math.Max(bm.MaxEmployees, (locations ?? 1) * bm.MaxEmployeesPerLocation);
            metrics.Add(new PlausibilityMetric("Employees", employees.ToString("N0"), $"<= {ceiling:N0}", employees > ceiling ? "Above industry ceiling" : "Normal"));
            if (employees > ceiling)
            {
                flags.Add(new PlausibilityFlag("HEADCOUNT_ABOVE_INDUSTRY_CEILING", $"{employees:N0} employees exceeds the plausible ceiling of {ceiling:N0} for this industry{(locations is null ? " (single entity, no locations declared)" : $" across {locations:N0} location(s)")}.", RiskTier.High));
                penalty += 25;
            }

            if (locations is int locs)
            {
                var perLocation = (double)employees / locs;
                metrics.Add(new PlausibilityMetric("Employees / location", perLocation.ToString("N1"), $"<= {bm.MaxEmployeesPerLocation:N0}", perLocation > bm.MaxEmployeesPerLocation ? "Above industry ceiling" : "Normal"));
                if (perLocation > bm.MaxEmployeesPerLocation && employees <= ceiling)
                {
                    flags.Add(new PlausibilityFlag("HEADCOUNT_HIGH_FOR_LOCATIONS", $"{perLocation:N0} employees per location exceeds the industry ceiling of {bm.MaxEmployeesPerLocation:N0}.", RiskTier.Medium));
                    penalty += 12;
                }
            }
        }

        // Tenure.
        if (d.YearsInBusiness is decimal years)
        {
            metrics.Add(new PlausibilityMetric("Years in business", years.ToString("N1"), "-", years < 1 ? "Start-up" : "Established"));
            if (years < 1 && volume > 1_000_000)
            {
                flags.Add(new PlausibilityFlag("STARTUP_WITH_LARGE_VOLUME", $"Business under one year old declaring {volume:N0} annual card volume.", RiskTier.High));
                penalty += 25;
            }
            else if (years < 2 && volume > 5_000_000)
            {
                flags.Add(new PlausibilityFlag("YOUNG_BUSINESS_LARGE_VOLUME", $"Business under two years old declaring {volume:N0} annual card volume.", RiskTier.Medium));
                penalty += 12;
            }
        }

        // Prior revenue (card volume should not exceed total revenue by much; some growth allowed).
        if (d.PriorYearRevenue is decimal prior && prior > 0)
        {
            var growth = volume / (double)prior;
            metrics.Add(new PlausibilityMetric("Declared card volume / prior-year revenue", growth.ToString("N2") + "x", "<= 1.5x", growth > 1.5 ? "Implies >50% growth or non-card revenue mis-stated" : "Consistent"));
            if (growth > 3)
            {
                flags.Add(new PlausibilityFlag("VOLUME_EXCEEDS_REVENUE", $"Declared card volume is {growth:N1}x last year's total revenue.", RiskTier.High));
                penalty += 30;
            }
            else if (growth > 1.5)
            {
                flags.Add(new PlausibilityFlag("AGGRESSIVE_GROWTH_ASSUMPTION", $"Declared card volume implies {(growth - 1) * 100:N0}% growth over last year's revenue.", RiskTier.Medium));
                penalty += 12;
            }
        }

        // Bank statements (card processor deposits).
        if (d.MonthlyCardVolumeFromStatements is decimal stmt && stmt > 0)
        {
            var annualised = (double)stmt * 12;
            var ratio = volume / annualised;
            metrics.Add(new PlausibilityMetric("Declared / statement-evidenced card volume", ratio.ToString("N2") + "x", "0.7x - 1.5x", ratio > 1.5 ? "Declared exceeds evidence" : ratio < 0.7 ? "Declared below evidence (possible load balancing)" : "Consistent"));
            if (ratio > 2.5)
            {
                flags.Add(new PlausibilityFlag("DECLARED_FAR_ABOVE_STATEMENTS", $"Declared volume is {ratio:N1}x the annualised card deposits seen in bank statements.", RiskTier.High));
                penalty += 30;
            }
            else if (ratio > 1.5)
            {
                flags.Add(new PlausibilityFlag("DECLARED_ABOVE_STATEMENTS", $"Declared volume is {ratio:N1}x the annualised card deposits seen in bank statements.", RiskTier.Medium));
                penalty += 12;
            }
            else if (ratio < 0.5)
            {
                flags.Add(new PlausibilityFlag("DECLARED_BELOW_STATEMENTS", "Existing card deposits are far above the declared volume; the merchant may be splitting volume across acquirers.", RiskTier.Medium));
                penalty += 10;
            }
        }

        // Catalogue size for e-commerce.
        if (d.WebsiteProductCount is int products && d.HasPhysicalLocation != true)
        {
            metrics.Add(new PlausibilityMetric("Website product count", products.ToString(), "-", products < 5 ? "Very thin catalogue" : "Normal"));
            if (products < 5 && volume > 500_000)
            {
                flags.Add(new PlausibilityFlag("THIN_CATALOGUE_LARGE_VOLUME", $"Online-only merchant with {products} product(s) declaring {volume:N0} annual volume.", RiskTier.High));
                penalty += 20;
            }
        }

        // Round-number declarations are a weak but real signal of guessed figures.
        if (volume >= 100_000 && volume % 1_000_000 == 0)
        {
            flags.Add(new PlausibilityFlag("ROUND_NUMBER_DECLARATION", "Annual volume is an exact multiple of 1,000,000; request supporting statements.", RiskTier.Low));
            penalty += 3;
        }

        var score = (int)Math.Round(Math.Clamp(100 - penalty, 0, 100));
        var verdict = score >= 75 ? "Plausible" : score >= 50 ? "Questionable" : "Implausible";
        return new VolumePlausibilityResult(score, verdict, metrics, flags, bm.Source);
    }
}
