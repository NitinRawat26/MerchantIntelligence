## Summary

<!-- What changed and why. -->

## Checklist

- [ ] `dotnet build -warnaserror` and `dotnet test` pass; `npm run build` passes if `web/workbench` changed
- [ ] **Wiki updated** (`docs/wiki/`) if this PR adds or changes any of:
  - a step, agent review or workflow field → `Agents-and-Steps.md`, `Assessment-Workflow.md`
  - an API endpoint or request/response shape → `API-Reference.md`
  - a UI route or page → `Web-UI.md`
  - an `appsettings` key or environment variable → `Configuration.md`
  - an external data source or reference file → `Data-Sources.md`
  - a score weight, threshold, rule, band or flag code → `Scoring-and-Decisions.md`, `KYB-and-Compliance.md`, `MCC-Validation.md`, `Underwriting.md`
- [ ] No wiki change needed (explain briefly): <!-- e.g. bug fix without behavioural change -->
