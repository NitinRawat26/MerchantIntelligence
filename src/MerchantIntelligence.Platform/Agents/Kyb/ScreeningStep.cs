using MerchantIntelligence.Kyb.Sanctions;
using MerchantIntelligence.Platform.Workflows;

namespace MerchantIntelligence.Platform.Agents.Kyb;

public sealed class ScreeningStep(SanctionsScreeningService screening) : IAssessmentStep
{
    public WorkflowStepDescriptor Descriptor { get; } = new("screening", "Sanctions / PEP / adverse-media screening",
        "Screens the business, trading name and every beneficial owner against the loaded sanctions and PEP lists plus adverse media.",
        [], [], false,
        [
            new("includeTradingName", "boolean", "true", "Also screen the trading name when it differs from the legal name."),
            new("includeOwners", "boolean", "true", "Screen each declared beneficial owner / principal.")
        ]);

    public async Task ExecuteAsync(AssessmentContext ctx)
    {
        var intake = ctx.Intake;
        var subjects = new List<ScreeningSubject> { new(intake.Business.LegalName, null, intake.Business.Country, false, "Business") };
        if (ctx.Param(Descriptor.Id, "includeTradingName", true) && !string.IsNullOrWhiteSpace(intake.Business.TradingName) && intake.Business.TradingName != intake.Business.LegalName)
            subjects.Add(new ScreeningSubject(intake.Business.TradingName, null, intake.Business.Country, false, "Trading name"));
        if (ctx.Param(Descriptor.Id, "includeOwners", true))
            subjects.AddRange(intake.Owners.Select(o => new ScreeningSubject(o.FullName, o.DateOfBirth, o.Nationality, true, o.Role ?? "Beneficial owner")));
        ctx.Screening = await ctx.RunAsync(Descriptor, () => screening.ScreenAsync(subjects, ctx.CancellationToken),
            s => $"{s.Subjects.Count} subject(s) screened · {s.Subjects.Count(x => x.PotentialMatch)} potential match(es) · " +
                 $"{s.Subjects.Sum(x => x.AdverseMedia?.NegativeCount ?? 0)} adverse-media article(s)" +
                 (s.Subjects.Any(x => x.AdverseMedia is { Succeeded: false }) ? " (media unavailable)" : "") +
                 $" · overall {s.OverallRisk}");
    }
}
