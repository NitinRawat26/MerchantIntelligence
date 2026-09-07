using MerchantIntelligence.MccValidation.Taxonomy;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MerchantIntelligence.Platform.Assessment;

/// <summary>Renders a completed assessment as a printable underwriting memo (QuestPDF, community licence).</summary>
public static class AssessmentPdfRenderer
{
    private static readonly object LicenseLock = new();
    private static bool _licensed;

    private const string Ink = "#1f2933";
    private const string Muted = "#616e7c";
    private const string Line = "#d9e2ec";
    private const string Approve = "#1b7f4b";
    private const string Refer = "#b35c00";
    private const string Decline = "#b3261e";

    public static byte[] Render(AssessmentResult r)
    {
        lock (LicenseLock)
        {
            if (!_licensed)
            {
                QuestPDF.Settings.License = LicenseType.Community;
                _licensed = true;
            }
        }

        var accent = r.Decision.Outcome switch { "Approve" => Approve, "Decline" => Decline, _ => Refer };

        return Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(t => t.FontSize(9.5f).FontColor(Ink).FontFamily(Fonts.Arial));

                page.Header().Column(col =>
                {
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text("Merchant Intelligence · Underwriting Assessment").FontSize(16).SemiBold();
                            c.Item().Text($"{r.Intake.Business.LegalName}{(r.Intake.Business.TradingName is { } t && t != r.Intake.Business.LegalName ? $" (t/a {t})" : "")}").FontSize(12).FontColor(Muted);
                        });
                        row.ConstantItem(150).AlignRight().Column(c =>
                        {
                            c.Item().Background(accent).Padding(6).AlignCenter().Text(r.Decision.Outcome.ToUpperInvariant()).FontColor(Colors.White).FontSize(14).Bold();
                            c.Item().PaddingTop(3).AlignCenter().Text($"Score {r.Decision.Score}/1000 · {r.Decision.Tier}").FontSize(9).FontColor(Muted);
                        });
                    });
                    col.Item().PaddingTop(6).LineHorizontal(1).LineColor(Line);
                });

                page.Content().PaddingTop(8).Column(col =>
                {
                    col.Spacing(10);

                    col.Item().Text(r.Explainability.Headline).FontSize(10.5f).SemiBold();
                    col.Item().Text(r.Decision.Summary);

                    KeyValues(col, "Assessment", new (string, string)[]
                    {
                        ("Reference", r.Id),
                        ("Completed", r.CompletedAt.ToString("yyyy-MM-dd HH:mm 'UTC'")),
                        ("Analyst", r.Intake.Actor),
                        ("Case", r.Case is null ? "not created" : $"{r.Case.Id} ({r.Case.Status}, {r.Case.Priority})"),
                        ("Rule set", $"v{r.Decision.RuleSetVersion}" + (r.Explainability.DecidingRule is { } d ? $" · deciding rule {d}" : "")),
                        ("Coverage", $"{r.Decision.CoveragePercent:F0}% of checks produced a signal")
                    });

                    KeyValues(col, "Merchant intake", new (string, string)[]
                    {
                        ("Legal name", r.Intake.Business.LegalName),
                        ("Registration / tax ID", Join(r.Intake.Business.RegistrationNumber, r.Intake.Business.TaxId)),
                        ("Address", r.Intake.Business.FullAddress),
                        ("Website", r.Intake.Business.WebsiteUrl ?? "—"),
                        ("Description", r.Intake.BusinessDescription ?? "—"),
                        ("MCC", r.Intake.MerchantCategoryCode.ToString()),
                        ("Declared volume", $"${r.Intake.AnnualVolume:N0} / year · avg ticket ${r.Intake.AverageTicket:N0} · max ${r.Intake.HighestTicket:N0}"),
                        ("Delivery / CNP", $"{(r.Intake.DeliveryDays is { } dd ? $"{dd} days" : "industry default")} · {r.Intake.CardNotPresentShare:P0} card-not-present"
                            + (r.Intake.OffersSubscriptions ? " · subscriptions" : "") + (r.Intake.OffersFreeTrials ? " · free trials" : "")),
                        ("Owners", r.Intake.Owners.Count == 0 ? "none declared" : string.Join("; ", r.Intake.Owners.Select(o => $"{o.FullName}{(o.OwnershipPercent is { } p ? $" {p:F0}%" : "")}{(o.Role is { } ro ? $" ({ro})" : "")}"))),
                        ("Documents", Join(r.Intake.BankStatementSource is { } bank ? $"bank: {bank}" : null, r.Intake.FinancialStatementSource is { } fin ? $"financials: {fin}" : null))
                    });

                    Section(col, "Check outcomes");
                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(c => { c.ConstantColumn(110); c.ConstantColumn(95); c.RelativeColumn(); c.ConstantColumn(48); });
                        t.Header(h =>
                        {
                            foreach (var s in new[] { "Check", "Result", "Detail", "Severity" })
                                h.Cell().Background("#f0f4f8").Padding(4).Text(s).SemiBold().FontSize(8.5f);
                        });
                        foreach (var o in r.Explainability.CheckOutcomes)
                        {
                            t.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(4).Text(o.Check).SemiBold();
                            t.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(4).Text(o.Result);
                            t.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(4).Text(o.Detail).FontSize(8.5f);
                            t.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(4).Text(o.Covered ? o.Severity.ToString() : "gap").FontColor(Severity(o.Severity, o.Covered)).SemiBold();
                        }
                    });

                    Section(col, "Unified risk score");
                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(c => { c.ConstantColumn(120); c.ConstantColumn(50); c.ConstantColumn(55); c.ConstantColumn(60); c.RelativeColumn(); });
                        t.Header(h =>
                        {
                            foreach (var s in new[] { "Component", "Weight", "Score", "Weighted", "Detail" })
                                h.Cell().Background("#f0f4f8").Padding(4).Text(s).SemiBold().FontSize(8.5f);
                        });
                        foreach (var c in r.Explainability.ScoreComponents)
                        {
                            t.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(4).Text(c.Name);
                            t.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(4).Text($"{c.Weight:P0}");
                            t.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(4).Text(c.Covered ? $"{c.Score:F0}/100" : "—");
                            t.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(4).Text(c.Covered ? $"{c.Weighted:F1}" : "gap").FontColor(c.Covered ? Ink : Refer);
                            t.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(4).Text(c.Detail).FontSize(8.5f);
                        }
                    });
                    if (r.Explainability.HardStops.Count > 0)
                        col.Item().Text($"Hard stops: {string.Join(", ", r.Explainability.HardStops)}").FontColor(Decline).SemiBold();
                    if (r.Explainability.CoverageGaps.Count > 0)
                        col.Item().Text($"Coverage gaps: {string.Join(", ", r.Explainability.CoverageGaps)}").FontColor(Refer);

                    if (r.Explainability.ReasonCodes.Count > 0)
                    {
                        Section(col, "Reason codes");
                        foreach (var rc in r.Explainability.ReasonCodes)
                            col.Item().Row(row =>
                            {
                                row.ConstantItem(150).Text(rc.Code).SemiBold().FontFamily(Fonts.CourierNew).FontSize(8.5f);
                                row.ConstantItem(50).Text(rc.Severity.ToString()).FontColor(Severity(rc.Severity, true));
                                row.RelativeItem().Text($"{rc.Description} ({rc.Source})").FontSize(8.5f);
                            });
                    }

                    if (r.Explainability.CreditContributions.Count > 0)
                    {
                        Section(col, "Credit model explainability (Shapley contributions)");
                        col.Item().Table(t =>
                        {
                            t.ColumnsDefinition(c => { c.ConstantColumn(130); c.ConstantColumn(90); c.ConstantColumn(90); c.ConstantColumn(70); c.RelativeColumn(); });
                            t.Header(h =>
                            {
                                foreach (var s in new[] { "Feature", "Value", "Baseline", "Contribution", "Direction" })
                                    h.Cell().Background("#f0f4f8").Padding(4).Text(s).SemiBold().FontSize(8.5f);
                            });
                            foreach (var c in r.Explainability.CreditContributions.OrderByDescending(x => Math.Abs(x.Contribution)))
                            {
                                t.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(4).Text(c.Feature);
                                t.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(4).Text(c.Value);
                                t.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(4).Text(c.BaselineValue).FontColor(Muted);
                                t.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(4).Text($"{(c.Contribution >= 0 ? "+" : "")}{c.Contribution:P1}").FontColor(c.Contribution >= 0 ? Approve : Decline);
                                t.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(4).Text(c.Direction);
                            }
                        });
                        if (r.CreditExplanation is { } ex) col.Item().Text(ex.Narrative).FontSize(8.5f).Italic();
                    }

                    if (r.Explainability.MatchedRules.Count > 0)
                    {
                        Section(col, "Policy rules");
                        foreach (var m in r.Explainability.MatchedRules)
                            col.Item().Row(row =>
                            {
                                row.ConstantItem(170).Text(m.Id).FontFamily(Fonts.CourierNew).FontSize(8.5f).SemiBold();
                                row.ConstantItem(60).Text(m.Outcome.ToString()).FontColor(m.Outcome.ToString() switch { "Decline" => Decline, "Approve" => Approve, _ => Refer });
                                row.RelativeItem().Text(m.Description ?? "").FontSize(8.5f);
                            });
                    }

                    Section(col, "Detailed narrative");
                    foreach (var p in r.Explainability.Narrative)
                        col.Item().Text(p).LineHeight(1.25f);

                    if (r.Explainability.Findings.Count > 0)
                    {
                        Section(col, "All findings");
                        foreach (var f in r.Explainability.Findings)
                            col.Item().Row(row =>
                            {
                                row.ConstantItem(50).Text(f.Severity.ToString()).FontColor(Severity(f.Severity, true)).SemiBold();
                                row.ConstantItem(170).Text(f.Code).FontFamily(Fonts.CourierNew).FontSize(8.5f);
                                row.RelativeItem().Text($"{f.Message} [{f.Source}]").FontSize(8.5f);
                            });
                    }

                    if (r.Terms is { } terms)
                    {
                        KeyValues(col, "Recommended commercial terms", new (string, string)[]
                        {
                            ("Risk band", $"{terms.RiskBand} (composite {terms.RiskScore:F2})"),
                            ("Reserve", $"{terms.Reserve.Type} · {terms.Reserve.RollingPercent:F1}% for {terms.Reserve.RollingDays} days" + (terms.Reserve.CapAmount > 0 ? $" · cap ${terms.Reserve.CapAmount:N0}" : "") + (terms.Reserve.UpfrontAmount > 0 ? $" · upfront ${terms.Reserve.UpfrontAmount:N0}" : "")),
                            ("Pricing", $"IC+ {terms.Pricing.InterchangePlusMarkupBps:F0} bps · ${terms.Pricing.PerTransactionFee:N2}/txn · ${terms.Pricing.MonthlyFee:N0}/month · chargeback fee ${terms.Pricing.ChargebackFee:N0}"),
                            ("Settlement", $"T+{terms.Pricing.SettlementDelayDays}"),
                            ("Caps", $"monthly ${terms.Pricing.MonthlyVolumeCap:N0} · single transaction ${terms.Pricing.SingleTransactionCap:N0}"),
                            ("Estimated exposure", $"${terms.EstimatedExposure:N0}"),
                            ("Factors", string.Join("; ", terms.Factors.Select(x => $"{x.Description} ({x.Effect})")))
                        });
                    }

                    if (r.Explainability.AnalystNextSteps.Count > 0)
                    {
                        Section(col, "Recommended analyst actions");
                        foreach (var s in r.Explainability.AnalystNextSteps)
                            col.Item().Row(row => { row.ConstantItem(12).Text("•"); row.RelativeItem().Text(s); });
                    }

                    Section(col, "Check execution log");
                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(c => { c.ConstantColumn(170); c.ConstantColumn(60); c.RelativeColumn(); c.ConstantColumn(50); });
                        foreach (var s in r.Steps)
                        {
                            t.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(3).Text(s.Name).FontSize(8.5f);
                            t.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(3).Text(s.Status.ToString()).FontSize(8.5f).FontColor(s.Status switch { StepStatus.Succeeded => Approve, StepStatus.Failed => Decline, _ => Muted });
                            t.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(3).Text(s.Error is null ? s.Summary : $"{s.Summary} {s.Error}").FontSize(8.5f);
                            t.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(3).AlignRight().Text($"{s.DurationMs} ms").FontSize(8.5f).FontColor(Muted);
                        }
                    });

                    col.Item().PaddingTop(6).Text("Data sources: GLEIF, SEC EDGAR, US Census geocoder, OFAC SDN, UN consolidated list, OpenSanctions, GDELT, RDAP and the merchant's own website. " +
                        "Where a source was unavailable the check is reported as a coverage gap and is never treated as clear. Model outputs are decision support, not a decision.")
                        .FontSize(7.5f).FontColor(Muted);
                });

                page.Footer().Row(row =>
                {
                    row.RelativeItem().Text($"{r.Id} · generated {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC").FontSize(8).FontColor(Muted);
                    row.RelativeItem().AlignRight().Text(t => { t.DefaultTextStyle(s => s.FontSize(8).FontColor(Muted)); t.Span("Page "); t.CurrentPageNumber(); t.Span(" of "); t.TotalPages(); });
                });
            });
        }).GeneratePdf();
    }

    private static void Section(ColumnDescriptor col, string title) =>
        col.Item().PaddingTop(4).BorderBottom(1).BorderColor(Line).PaddingBottom(2).Text(title).FontSize(11).SemiBold();

    private static void KeyValues(ColumnDescriptor col, string title, IEnumerable<(string Key, string Value)> rows)
    {
        Section(col, title);
        col.Item().Table(t =>
        {
            t.ColumnsDefinition(c => { c.ConstantColumn(130); c.RelativeColumn(); });
            foreach (var (k, v) in rows)
            {
                t.Cell().Padding(3).Text(k).FontColor(Muted);
                t.Cell().Padding(3).Text(string.IsNullOrWhiteSpace(v) ? "—" : v);
            }
        });
    }

    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p))) is { Length: > 0 } s ? s : "—";

    private static string Severity(RiskTier tier, bool covered) => !covered ? Refer : tier switch { RiskTier.High => Decline, RiskTier.Medium => Refer, _ => Approve };
}
