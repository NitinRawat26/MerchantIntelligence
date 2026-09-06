---
name: testing-merchantintelligence-kyb
description: Run live KYB API acceptance checks with Swagger and curl, preserving public-source limitations.
---

# Runtime KYB testing

Use the repo's .NET 8 blueprint and `dotnet run --project src/MerchantIntelligence.Api`.
Check port 5292 before starting; stop an older API before rebuilding after edits.
Swagger is at http://localhost:5292/swagger. These API routes do not require authentication.

## Devin Secrets Needed
None for the default suite. `Kyb:OpenCorporatesApiToken` and
`Kyb:CompaniesHouseApiKey` are optional: absent keys should produce explicit
Not configured source results, not block other registries.

## Reliable evidence
- Confirm request field names from `/swagger/v1/swagger.json`. Screening uses
  `isIndividual`; adverse-media result uses `succeeded`. Reports nest `business` and `owners`.
- In Swagger, expand the operation, click Try it out, replace the complete JSON,
  then Execute. Inspect **Server response**, not static Responses/Example Value.
- Long JSON responses scroll internally. Click outside the response before
  Ctrl+Home to scroll the page; browser Find can locate a specific result/flag.
- Save curl requests, responses, HTTP status and timing alongside screenshots.
- Rebuild/restart after working-tree updates; state precisely which checks were
  rerun and which unchanged assertions were retained.

## Public-source caveats
- Initial sanctions loading can take a minute and download ~100 MB. Cache is
  normally beneath API bin/Debug/net8.0/data/sanctions. State explicitly when
  testing loaded existing files instead of exercising a cold download.
- GDELT may rate-limit. Require HTTP200 with structured `succeeded=false` and
  an explanatory error, not an API500. No articles returned is not proof that
  a live article-success path was exercised.
- Apple registry verification can succeed through SEC while Census returns
  ADDRESS_UNVERIFIED; evaluate per-source results instead of only overall status.
- Low declared owner coverage causes UBO_COVERAGE_LOW and Medium combined risk.
  The README's 0.01% example should not be expected to produce Low overall risk.
- Use positive and benign sanctions controls together; distinguish hit count,
  match confidence, flag severity and aggregate risk.
- Treat existing MCC-validator response-contract checks separately from its
  classifier accuracy; do not infer classification quality from HTTP200.
