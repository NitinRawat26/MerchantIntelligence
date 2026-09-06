---
name: underwriting-api-testing
description: Test local underwriting explanations, terms, and statement uploads through Swagger and deterministic curl fixtures.
---

# Local setup
- Use `dotnet run --project src/MerchantIntelligence.Api` from the repo root. API-only startup isolates unrelated projects under active development.
- Swagger is at `http://127.0.0.1:5292/swagger/index.html`; expand Underwriting operations and use Try it out / Execute. Inspect the actual Server response, not static Example Value.
- Restart the API after source changes; merely rebuilding does not replace the running process.

# Devin Secrets Needed
None for local underwriting endpoints.

# Runtime checks
- Confirm live Swagger schemas before writing assertions. Credit prediction uses `probabilitiesPercent` (0–100); explanation probabilities are fractions. Volume plausibility uses `plausibilityScore`.
- Test all explainClass values and check six Shapley contributions sum to predicted minus baseline within rounding tolerance (0.0004). Check factual reason codes independently of class-specific contribution direction.
- Probe fractional range violations, not only integers: CNP share -0.1/1.1 must reject, 0/1 must accept.
- Use two-month synthetic bank CSV with independently calculated inflows, outflows and processor deposits. Implied annual card volume grosses up monthly processor deposits for 2.9% fees, rather than simply multiplying by twelve.
- Compare inline CSV to multipart comma/semicolon/debit-credit CSV and text PDF. Preserve file fixtures, request bodies, headers, HTTP statuses, and responses.
- Create image-only PDF separately from text PDF. No OCR is supported: expect no fabricated transactions and a structured scanned/unsupported-layout explanation, which can be returned as HTTP400 ProblemDetails rather than a successful warnings array.
- Financial `CARD_VOLUME_EXCEEDS_REVENUE` uses an intentional >120% threshold, allowing gross/net and period differences. Verify both a below-threshold negative control and above-threshold positive control.
- Documents generated for testing establish controlled parser behavior, not coverage of every real bank layout.
