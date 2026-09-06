using System.Text;
using MerchantIntelligence.CreditDecision;
using MerchantIntelligence.Kyb;
using MerchantIntelligence.MccValidation.Taxonomy;
using MerchantIntelligence.Underwriting.Benchmarks;
using MerchantIntelligence.Underwriting.Explainability;
using MerchantIntelligence.Underwriting.Financials;
using MerchantIntelligence.Underwriting.Plausibility;
using MerchantIntelligence.Underwriting.Pricing;

namespace MerchantIntelligence.Tests;

/// <summary>Deterministic stand-in for the LightGBM model: a logistic score over the six features.</summary>
internal sealed class RuleBasedPredictor : IDecisionPredictor
{
    public DecisionResult Predict(MerchantApplication a)
    {
        var z = 1.5
                - (a.MatchFound ? 3.0 : 0)
                + (a.ExistingRelationship ? 1.0 : 0)
                - (a.MerchantCategoryCode is 7995 or 5967 or 4816 or 6051 ? 1.5 : 0)
                - Math.Max(0, Math.Log10(Math.Max(1, a.AverageTicket)) - 2) * 1.5
                - Math.Max(0, Math.Log10(Math.Max(1, a.HighestTicket)) - 3)
                - Math.Max(0, Math.Log10(Math.Max(1, a.AnnualVolume)) - 6.5) * 0.5;
        var approve = 1 / (1 + Math.Exp(-z));
        var decline = (1 - approve) * 0.8;
        var cancel = (1 - approve) * 0.2;
        var probs = new Dictionary<Decision, double>
        {
            [Decision.Approved] = approve,
            [Decision.Declined] = decline,
            [Decision.Cancelled] = cancel
        };
        var best = probs.MaxBy(kv => kv.Value);
        return new DecisionResult(best.Key, best.Value, probs);
    }
}

public sealed class DecisionExplainerTests
{
    private static readonly DecisionExplainer Explainer = new(new RuleBasedPredictor());

    private static readonly MerchantApplication Risky = new()
    {
        MerchantCategoryCode = 7995, AnnualVolume = 5_000_000, AverageTicket = 900, HighestTicket = 20_000,
        MatchFound = true, ExistingRelationship = false
    };

    [Fact]
    public void Contributions_sum_to_predicted_minus_baseline()
    {
        var e = Explainer.Explain(Risky, Decision.Approved);
        var sum = e.Contributions.Sum(c => c.Contribution);
        Assert.Equal(e.PredictedProbability - e.BaselineProbability, sum, 4);
        Assert.Equal(6, e.Contributions.Count);
    }

    [Fact]
    public void Feature_identical_to_baseline_has_zero_contribution()
    {
        var e = Explainer.Explain(DecisionExplainer.DefaultBaseline, Decision.Approved);
        Assert.All(e.Contributions, c => Assert.Equal(0, c.Contribution, 6));
        Assert.Equal(e.BaselineProbability, e.PredictedProbability, 6);
    }

    [Fact]
    public void Match_listing_is_top_negative_driver_with_reason_code()
    {
        var e = Explainer.Explain(Risky, Decision.Approved);
        var top = e.Contributions.OrderBy(c => c.Contribution).First();
        Assert.Equal(nameof(MerchantApplication.MatchFound), top.Feature);
        Assert.Contains(e.ReasonCodes, r => r.Code == "MATCH_LISTED");
        Assert.Contains(e.ReasonCodes, r => r.Code == "HIGH_RISK_MCC");
        Assert.Equal(Decision.Declined, e.Decision);
        Assert.False(string.IsNullOrWhiteSpace(e.Narrative));
    }

    [Fact]
    public void Explained_class_defaults_to_predicted_decision()
    {
        var e = Explainer.Explain(Risky);
        Assert.Equal(e.Decision, e.ExplainedClass);
        Assert.True(e.PredictedProbability > 0.5);
    }

    [Fact]
    public void Existing_relationship_contributes_positively()
    {
        var app = DecisionExplainer.DefaultBaseline;
        app.ExistingRelationship = true;
        var e = Explainer.Explain(app, Decision.Approved);
        var rel = e.Contributions.Single(c => c.Feature == nameof(MerchantApplication.ExistingRelationship));
        Assert.True(rel.Contribution > 0);
        Assert.Equal("Increases", rel.Direction);
        Assert.Contains(e.ReasonCodes, r => r.Code == "EXISTING_RELATIONSHIP");
    }
}

public sealed class IndustryBenchmarksTests
{
    [Theory]
    [InlineData(5812, "MCC 5812")]
    [InlineData(7995, "MCC 7995")]
    [InlineData(5411, "MCC 5411")]
    public void Mcc_overrides_take_precedence(int mcc, string expectedSourcePrefix)
    {
        var b = IndustryBenchmarks.Default.Resolve(mcc);
        Assert.StartsWith(expectedSourcePrefix, b.Source);
        Assert.True(b.TicketP90 > b.TicketP10);
        Assert.True(b.RevenuePerEmployeeP90 > b.RevenuePerEmployeeP50);
        Assert.True(b.RevenuePerEmployeeP50 > b.RevenuePerEmployeeP10);
    }

    [Fact]
    public void Unknown_mcc_falls_back_to_catalog_category_or_all_industry()
    {
        var b = IndustryBenchmarks.Default.Resolve(null);
        Assert.Contains("All", b.Source, StringComparison.OrdinalIgnoreCase);
        var catalogBased = IndustryBenchmarks.Default.Resolve(5311);
        Assert.False(string.IsNullOrEmpty(catalogBased.Source));
        Assert.True(catalogBased.ChargebackRate is > 0 and < 0.1);
    }

    [Fact]
    public void Gambling_carries_higher_chargeback_rate_than_grocery()
    {
        Assert.True(IndustryBenchmarks.Default.Resolve(7995).ChargebackRate > IndustryBenchmarks.Default.Resolve(5411).ChargebackRate);
    }
}

public sealed class VolumePlausibilityTests
{
    private static readonly VolumePlausibilityAnalyzer Analyzer = new(IndustryBenchmarks.Default);

    [Fact]
    public void Consistent_restaurant_declaration_is_plausible()
    {
        var r = Analyzer.Analyze(new VolumeDeclaration(480_500, 42, 380, 5812, EmployeeCount: 8, YearsInBusiness: 6, PriorYearRevenue: 455_000,
            MonthlyCardVolumeFromStatements: 38_000, HasPhysicalLocation: true));
        Assert.True(r.PlausibilityScore >= 80, $"score {r.PlausibilityScore}: {string.Join(", ", r.Flags.Select(f => f.Code))}");
        Assert.DoesNotContain(r.Flags, f => f.Severity == RiskTier.High);
    }

    [Fact]
    public void Startup_with_huge_volume_and_tiny_headcount_is_flagged()
    {
        var r = Analyzer.Analyze(new VolumeDeclaration(20_000_000, 50, 500, 5812, EmployeeCount: 1, YearsInBusiness: 0.2m));
        Assert.Contains(r.Flags, f => f.Code == "VOLUME_EXCEEDS_HEADCOUNT_CAPACITY");
        Assert.Contains(r.Flags, f => f.Code == "STARTUP_WITH_LARGE_VOLUME");
        Assert.Contains(r.Flags, f => f.Code == "ROUND_NUMBER_DECLARATION");
        Assert.True(r.PlausibilityScore < 50);
        Assert.NotEqual("Plausible", r.Verdict);
    }

    [Fact]
    public void Declared_volume_far_above_bank_statements_is_high_severity()
    {
        var r = Analyzer.Analyze(new VolumeDeclaration(2_400_000, 100, 1000, 5999, MonthlyCardVolumeFromStatements: 40_000));
        var flag = Assert.Single(r.Flags, f => f.Code == "DECLARED_FAR_ABOVE_STATEMENTS");
        Assert.Equal(RiskTier.High, flag.Severity);
    }

    [Fact]
    public void Ticket_far_above_industry_and_few_transactions_flagged()
    {
        var r = Analyzer.Analyze(new VolumeDeclaration(60_000, 15_000, 15_000, 5812));
        Assert.Contains(r.Flags, f => f.Code == "TICKET_FAR_ABOVE_INDUSTRY");
        Assert.Contains(r.Flags, f => f.Code == "IMPLAUSIBLY_FEW_TRANSACTIONS");
    }

    [Fact]
    public void Thin_catalogue_online_only_flagged()
    {
        var r = Analyzer.Analyze(new VolumeDeclaration(3_000_000, 80, 500, 5999, WebsiteProductCount: 2, HasPhysicalLocation: false));
        Assert.Contains(r.Flags, f => f.Code == "THIN_CATALOGUE_LARGE_VOLUME");
    }

    [Fact]
    public void Volume_exceeding_prior_revenue_flagged()
    {
        var r = Analyzer.Analyze(new VolumeDeclaration(1_500_000, 80, 500, 5999, PriorYearRevenue: 300_000, YearsInBusiness: 4));
        Assert.Contains(r.Flags, f => f.Code is "VOLUME_EXCEEDS_REVENUE" or "AGGRESSIVE_GROWTH_ASSUMPTION");
    }
}

public sealed class ReservePricingRecommenderTests
{
    private static readonly ReservePricingRecommender Recommender = new(new RuleBasedPredictor(), IndustryBenchmarks.Default, MccCatalog.Default);

    private static MerchantApplication LowRisk => new()
    {
        MerchantCategoryCode = 5411, AnnualVolume = 600_000, AverageTicket = 40, HighestTicket = 250, ExistingRelationship = true
    };

    private static MerchantApplication HighRisk => new()
    {
        MerchantCategoryCode = 7995, AnnualVolume = 6_000_000, AverageTicket = 800, HighestTicket = 15_000, MatchFound = true
    };

    [Fact]
    public void Low_risk_card_present_grocer_gets_band_A_or_B_without_reserve_cap()
    {
        var t = Recommender.Recommend(new PricingInput(LowRisk, DeliveryDays: 0, CardNotPresentShare: 0.05));
        Assert.Contains(t.RiskBand, new[] { "A", "B" });
        Assert.True(t.Reserve.RollingPercent <= 5);
        Assert.True(t.Pricing.InterchangePlusMarkupBps < 60);
        Assert.Equal(0, t.Reserve.UpfrontAmount);
    }

    [Fact]
    public void Match_listed_high_risk_merchant_gets_band_E_with_heavy_reserve()
    {
        var t = Recommender.Recommend(new PricingInput(HighRisk, DeliveryDays: 30, KybHighRisk: true, WebsiteComplianceScore: 30, OffersFreeTrials: true));
        Assert.Equal("E", t.RiskBand);
        Assert.True(t.Reserve.RollingPercent >= 20);
        Assert.True(t.Reserve.RollingDays >= 180);
        Assert.True(t.Reserve.UpfrontAmount > 0);
        Assert.True(t.Pricing.SettlementDelayDays >= 3);
        Assert.Contains(t.Factors, f => f.Code == "MODEL_RISK");
        Assert.Contains(t.Factors, f => f.Code == "KYB_HIGH_RISK");
        Assert.Contains(t.Factors, f => f.Code == "WEBSITE_NON_COMPLIANT");
    }

    [Fact]
    public void Risk_score_is_monotone_in_risk_signals()
    {
        var baseline = Recommender.Recommend(new PricingInput(LowRisk));
        var worse = Recommender.Recommend(new PricingInput(LowRisk, DeliveryDays: 60, KybHighRisk: true, WebsiteComplianceScore: 20, VolumePlausibilityScore: 20));
        Assert.True(worse.RiskScore > baseline.RiskScore);
        Assert.True(worse.Reserve.RollingPercent >= baseline.Reserve.RollingPercent);
        Assert.True(worse.Pricing.InterchangePlusMarkupBps >= baseline.Pricing.InterchangePlusMarkupBps);
        Assert.True(worse.EstimatedExposure > baseline.EstimatedExposure);
    }

    [Fact]
    public void Exposure_scales_with_delivery_days()
    {
        var fast = Recommender.Recommend(new PricingInput(LowRisk, DeliveryDays: 1));
        var slow = Recommender.Recommend(new PricingInput(LowRisk, DeliveryDays: 90));
        Assert.True(slow.EstimatedExposure > fast.EstimatedExposure);
        Assert.Contains(slow.Factors, f => f.Code.Contains("DELIVERY", StringComparison.Ordinal));
    }

    [Fact]
    public void Steady_state_balance_reflects_rolling_terms()
    {
        var t = Recommender.Recommend(new PricingInput(HighRisk));
        var monthly = (decimal)HighRisk.AnnualVolume / 12;
        var expected = monthly * (decimal)(t.Reserve.RollingPercent / 100) * (t.Reserve.RollingDays / 30m);
        if (t.Reserve.CapAmount > 0) expected = Math.Min(expected, t.Reserve.CapAmount);
        Assert.Equal((double)expected, (double)t.Reserve.EstimatedSteadyStateBalance, 0);
    }
}

public sealed class BankStatementParserTests
{
    private const string Csv = """
        Date,Description,Debit,Credit,Balance
        2024-01-02,"STRIPE TRANSFER ST-1",,4200.00,14200.00
        2024-01-03,"PAYROLL GUSTO",3100.00,,11100.00
        2024-01-05,"NSF FEE",35.00,,11065.00
        2024-01-08,"SQUARE INC 240108P2",,2200.50,13265.50
        2024-01-15,"SBA LOAN PAYMENT",1500.00,,11765.50
        2024-02-01,"STRIPE TRANSFER ST-2",,5100.00,16865.50
        2024-02-03,"OWNER DRAW",2000.00,,14865.50
        2024-02-10,"RETURNED ITEM",120.00,,14745.50
        2024-03-01,"STRIPE TRANSFER ST-3",,4700.00,19445.50
        2024-03-04,"RENT PAYMENT",3000.00,,16445.50
        """;

    [Fact]
    public void Parses_debit_credit_csv_with_balances()
    {
        var parsed = BankStatementParser.ParseCsv(Csv);
        Assert.Equal(10, parsed.Transactions.Count);
        Assert.Equal("csv", parsed.Format);
        Assert.Empty(parsed.Warnings);
        var stripe = parsed.Transactions.First();
        Assert.Equal(new DateOnly(2024, 1, 2), stripe.Date);
        Assert.Equal(4200.00m, stripe.Amount);
        Assert.Equal(14200.00m, stripe.Balance);
        Assert.Equal(-3100.00m, parsed.Transactions[1].Amount);
    }

    [Fact]
    public void Parses_single_signed_amount_column_and_dmy_dates()
    {
        const string csv = """
            Posting Date;Details;Amount
            05/01/2024;"CARD SETTLEMENT PAYPAL";1,250.00
            06/01/2024;"UTILITIES";-300.25
            """;
        var parsed = BankStatementParser.ParseCsv(csv);
        Assert.Equal(2, parsed.Transactions.Count);
        Assert.Equal(1250.00m, parsed.Transactions[0].Amount);
        Assert.Equal(-300.25m, parsed.Transactions[1].Amount);
        Assert.Equal(new DateOnly(2024, 1, 5), parsed.Transactions[0].Date);
    }

    [Fact]
    public void Parenthesised_and_currency_amounts_are_parsed()
    {
        Assert.Equal(-1234.56m, BankStatementParser.TryParseAmount("($1,234.56)"));
        Assert.Equal(99.9m, BankStatementParser.TryParseAmount("£99.90"));
        Assert.Null(BankStatementParser.TryParseAmount("n/a"));
    }

    [Fact]
    public void Unrecognisable_csv_yields_warning_not_exception()
    {
        var parsed = BankStatementParser.ParseCsv("foo,bar\nbaz,qux\n");
        Assert.Empty(parsed.Transactions);
        Assert.NotEmpty(parsed.Warnings);
    }

    [Fact]
    public void Text_only_pdf_stream_that_is_not_a_pdf_is_reported()
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("not a pdf"));
        var ex = Record.Exception(() => BankStatementParser.Parse(ms, "statement.pdf"));
        Assert.NotNull(ex);
    }

    [Fact]
    public void Cash_flow_analysis_detects_processors_and_risk_events()
    {
        var a = CashFlowAnalyzer.Analyze(BankStatementParser.ParseCsv(Csv));
        Assert.Equal(3, a.MonthsCovered);
        Assert.Contains("Stripe", a.DetectedProcessors);
        Assert.Contains("Square", a.DetectedProcessors);
        Assert.Equal(16200.50m, a.TotalInflows);
        Assert.Equal(1, a.NsfOrOverdraftCount);
        Assert.Equal(1, a.ReturnedItemCount);
        Assert.Equal(1500m, a.LoanRepayments);
        Assert.Equal(3100m, a.Payroll);
        Assert.Equal(2000m, a.OwnerDraws);
        Assert.Equal(Math.Round(16200.50m / 3 * 12 / 0.971m, 0), a.ImpliedAnnualCardVolume);
        Assert.DoesNotContain(a.Flags, f => f.Code == "MULTIPLE_PROCESSORS");
        Assert.Contains(a.Flags, f => f.Code == "NSF_PRESENT");
        Assert.Equal(3, a.Monthly.Count);
        Assert.Equal(11065.00m, a.MinimumBalance);
    }

    [Fact]
    public void Negative_balances_are_counted()
    {
        const string csv = """
            Date,Description,Amount,Balance
            2024-01-02,Deposit,500,500
            2024-01-03,Rent,-900,-400
            2024-01-04,Fees,-20,-420
            2024-01-05,Deposit,1000,580
            """;
        var a = CashFlowAnalyzer.Analyze(BankStatementParser.ParseCsv(csv));
        Assert.Equal(2, a.NegativeBalanceDays);
        Assert.Contains(a.Flags, f => f.Code == "NEGATIVE_BALANCE_DAYS");
    }
}

public sealed class ProfitAndLossAnalyzerTests
{
    private const string Healthy = """
        Revenue,1,200,000
        Cost of Goods Sold,600,000
        Gross Profit,600,000
        Operating Expenses,450,000
        Operating Income,150,000
        Interest Expense,10,000
        Depreciation,20,000
        Net Income,110,000
        Total Assets,800,000
        Current Assets,300,000
        Cash,120,000
        Total Liabilities,300,000
        Current Liabilities,150,000
        Total Debt,120,000
        Total Equity,500,000
        """;

    [Fact]
    public void Computes_ratios_for_healthy_statement()
    {
        var a = ProfitAndLossAnalyzer.AnalyzeText(Healthy);
        Assert.Equal(1_200_000m, a.Statement.Revenue);
        Assert.Equal(110_000m, a.Statement.NetIncome);
        Assert.Equal(0.5, a.Ratios.Single(r => r.Name == "Gross margin").Value);
        Assert.Equal(2.0, a.Ratios.Single(r => r.Name.StartsWith("Current ratio")).Value);
        Assert.Equal(17.0, a.Ratios.Single(r => r.Name.StartsWith("EBITDA")).Value);
        Assert.Empty(a.Flags);
        Assert.Empty(a.Warnings);
    }

    [Fact]
    public void Loss_making_illiquid_negative_equity_statement_is_flagged()
    {
        const string text = """
            Sales                 400,000
            COGS                  380,000
            Operating expenses    90,000
            Interest expense      40,000
            Net loss              (110,000)
            Current assets        20,000
            Current liabilities   90,000
            Total liabilities     300,000
            Shareholders' equity  (50,000)
            """;
        var a = ProfitAndLossAnalyzer.AnalyzeText(text);
        var codes = a.Flags.Select(f => f.Code).ToHashSet();
        Assert.Contains("LOSS_MAKING", codes);
        Assert.Contains("THIN_GROSS_MARGIN", codes);
        Assert.Contains("WEAK_DEBT_COVERAGE", codes);
        Assert.Contains("ILLIQUID", codes);
        Assert.Contains("NEGATIVE_EQUITY", codes);
    }

    [Fact]
    public void Card_volume_exceeding_revenue_flagged()
    {
        var a = ProfitAndLossAnalyzer.AnalyzeText(Healthy, declaredAnnualCardVolume: 2_500_000);
        Assert.Contains(a.Flags, f => f.Code == "CARD_VOLUME_EXCEEDS_REVENUE");
    }

    [Fact]
    public void Unrecognised_text_produces_warning()
    {
        var a = ProfitAndLossAnalyzer.AnalyzeText("hello world\nnothing here");
        Assert.NotEmpty(a.Warnings);
        Assert.Null(a.Statement.Revenue);
    }
}
