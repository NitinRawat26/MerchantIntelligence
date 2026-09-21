# KYB and compliance

Project: `src/MerchantIntelligence.Kyb`. API: `/api/kyb` (see [API reference](API-Reference.md)).
All sources are public/free by default; keyed sources are opt-in ([Configuration](Configuration.md)).
Live acceptance procedure: `.agents/skills/kyb-api-testing/SKILL.md`.

## Business identity verification — `Registry/`

`BusinessVerificationService` fans the declared legal name / address / registration number out to
every configured `IRegistryProvider`, fuzzy-matches the returned `RegistryRecord`s
(`Matching/NameMatcher`: token-set overlap + Jaro-Winkler, legal suffixes stripped) and returns a
`VerificationResult` with status (`Verified` / `PartialMatch` / `NotFound` / `Unavailable`),
confidence, per-source outcome and flags.

| Provider | Data | Key |
|----------|------|-----|
| GLEIF | LEI records: legal name, legal form, address, status | no |
| SEC EDGAR | company-name autocomplete (`efts.sec.gov`) → `data.sec.gov/submissions/CIK….json`: entity type, SIC, state of incorporation, EIN, website, last filing | no (needs descriptive `Kyb:UserAgent`) |
| US Census geocoder | address normalisation / geocoding | no |
| OpenCorporates | company type, status, incorporation date | `Kyb:OpenCorporatesApiToken` |
| Companies House (UK) | company status, SIC, registered office | `Kyb:CompaniesHouseApiKey` |
| Kentucky SOS (`KentuckySosRegistryProvider`, `Local`, KY applicants only) | legal name, status, **standing**, organisation date, principal office, company type, industry, employee band, county, last annual report, managed-by, registered agent, assumed names | no (`Kyb:StateRegistriesEnabled`) |

Flags: `NEW_ENTITY`, `NAME_MISMATCH`, `REGISTERED_ADDRESS_MISMATCH`, `INACTIVE_ENTITY`,
`VIRTUAL_OFFICE_ADDRESS`, `ENTITY_NOT_FOUND`, plus state-register facts: `REGISTRY_BAD_STANDING`,
`REGISTRY_HEADCOUNT_MISMATCH`, `REGISTRY_INDUSTRY_MISMATCH`, `REGISTRY_ANNUAL_REPORT_STALE`,
`REGISTRY_ASSUMED_NAME_MATCH` (see [steps/verification.md](../steps/verification.md)). A local
provider whose jurisdiction does not cover the applicant is omitted, not counted as a miss. The
registered agent is displayed but never promoted to a beneficial owner. A source that errors is *Unavailable* and excluded; if
every source is unavailable the whole check is *Unavailable*, never "verified".

**Registry scope.** Each provider declares a `RegistryReach` (`Global`: GLEIF, SEC EDGAR; `Local`:
OpenCorporates, Companies House) and `VerifyAsync(identity, scope)` takes a `RegistryQueryScope`
chosen by the Profile agent from the legal form ([steps/profile.md](../steps/profile.md)):

| Scope | Who | Behaviour |
|---|---|---|
| `All` / `Global` | public corporations, C-Corps ≥ $10M, unknown legal form | Every provider; a miss everywhere is `NotFound` → `BUSINESS_UNVERIFIED` |
| `Local` | LLCs, partnerships, S-Corps, trusts, private C-Corps | Company registers are authoritative: hit → `Verified`, miss → `NotFound`; no local provider configured → `Inconclusive` + `LOCAL_REGISTRY_UNAVAILABLE`. GLEIF / EDGAR silence alone can never fail the merchant |
| `TaxExempt` | non-profits | Company registers queried with the scope recorded (a tax-exempt provider is not wired yet) |
| `None` | sole proprietorships, public bodies | Nothing queried; `VerificationStatus.NotApplicable`, identity rests on the owner, local presence and bank evidence |

The result carries the `Scope` used. Ticker symbols are still not captured. Small merchants that
formerly came back `NotFound` from GLEIF / EDGAR are now `Inconclusive` or verified through their
company register; *local presence* below remains the complementary evidence.

## Local business presence — `Registry/LocalPresence.cs`

`LocalPresenceService` (step `presence`, depends on `verification`):

1. Geocode the declared address — US Census first, OpenStreetMap Nominatim fallback.
2. Search places sources around that point for a business with a matching name:
   **OpenStreetMap Overpass** (always), **Foursquare Places** (`Kyb:FoursquareApiKey`),
   **Google Places** (`Kyb:GooglePlacesApiKey`).
3. Score name similarity + distance → `Confirmed` / `Partial` / `NotFound`.

Effects: a confirmed presence raises identity to `PartialMatch` (confidence capped at 70 % — it
proves trading at the location, not legal registration) and adds `LOCAL_PRESENCE_CONFIRMED`; near
miss → `LOCAL_PRESENCE_PARTIAL`; nothing nearby → `LOCAL_PRESENCE_NOT_FOUND` (Low severity: with
OSM alone, absence is weak evidence). `Kyb:LocalPresenceEnabled=false` disables it.

The result also carries **address type** (`Residential` / `Commercial` / `MixedUse` / `Cmra` /
`Unknown`, from PO-box / unit text, Nominatim category, OSM `building=` / `landuse=` tags, nearby
POIs and CMRA operators, checked against the MCC), venue **reputation** from Foursquare / Google
(rating, count, popularity, listed-since, open-now → `LOCAL_PRESENCE_REPUTATION`,
`LOCAL_PRESENCE_LOW_RATING`) and a **digital footprint** from the contact e-mail domain via RDAP
(`EMAIL_FREEMAIL`, `EMAIL_DOMAIN_NEW`, `EMAIL_DOMAIN_TENURE`, `EMAIL_DOMAIN_UNRESOLVED`,
`EMAIL_DOMAIN_MISMATCH`). See [steps/presence.md](../steps/presence.md).

## Owner identity, bank evidence and licences (Platform)

Three SMB-oriented checks live in the Platform project rather than the Kyb library because they
read the intake and the profile rather than an external source:

- **`owners`** — `OwnerIdentityAssessor` / `PrincipalRegistry`: completeness (DOB, nationality,
  address, role, ownership %), owner age vs. years in business, owner vs. business address,
  cross-application duplicate / velocity ([steps/owners.md](../steps/owners.md)).
- **`bank`** — for Micro / Small the statement is *required* evidence: holder name vs. legal /
  trading / owner name, inflows and card deposits vs. declared volume, processor payouts, dry
  months ([steps/bank.md](../steps/bank.md)).
- **`licensing`** — `LicensingAssessor` maps regulated MCCs (food service, alcohol, tobacco,
  pharmacy, healthcare, legal, personal care, child care, MSB, gaming, firearms, transport,
  contractors, lodging) to required licence types and checks the analyst's attestations for
  missing / expired / future-issued / incomplete / unevidenced / expiring. Attested means "seen and
  transcribed", never issuer-verified; a Secretary of State registration never satisfies a licence
  requirement ([steps/licensing.md](../steps/licensing.md)).

## Sanctions / PEP / adverse-media screening — `Sanctions/`

* `SanctionsListSources`: OpenSanctions consolidated `targets.simple.csv` (OFAC, EU, UN, UK HMT…),
  OFAC SDN CSV, UN Security Council consolidated XML; optional OpenSanctions PEP dataset
  (`Sanctions:IncludePeps`). Downloaded on first use into `Sanctions:CacheDirectory`
  (default `data/sanctions`, ~100 MB, 30–60 s) and refreshed every `Sanctions:RefreshInterval` (24 h).
* `SanctionsIndex` builds an in-memory index; `SanctionsScreeningService` screens the legal name,
  trading name and each beneficial owner (aliases, DOB and nationality aware) with
  `Sanctions:MatchThreshold` (0.85) and runs a multi-source adverse-media search
  (`Sanctions:EnableAdverseMedia`, sources in `Sanctions:AdverseMediaSources`).
* Result: `Clear` / `PotentialMatch` / `Match` / `Unavailable` with per-hit list, score and source
  URL. A `Match` sets the `SANCTIONS_MATCH` hard stop. If no list could be loaded the result is
  *Unavailable*, not clear. The KYB agent re-screens any alias discovered by registry verification
  (`ALIAS_RESCREENED`).

### Adverse media — `CompositeAdverseMediaProvider`

All sources are free and keyless; they run **in parallel per subject** and are merged:

| Key | Source | Query | Notes |
|---|---|---|---|
| `gdelt` | GDELT DOC 2.0 (`ArtList`, last 3 months, tone) | `"<name>" (16 query terms)` | rate-limited ~1 req / 5 s per IP → calls are serialised process-wide with a 5.2 s gap and one retry on HTTP 429; persistent throttling backs the source off for 60 s. Every source is also bounded by `Sanctions:AdverseMediaSourceTimeoutSeconds` (12 s) per subject |
| `googlenews` | Google News RSS | `"<name>" (16 query terms)` | headlines + snippets, unlimited |
| `bingnews` | Bing News RSS | `"<name>" (first 8 query terms)` | headlines + snippets |
| `wikipedia` | MediaWiki search API | `"<name>" first 10 query terms` | encyclopaedic snippets (controversies, legal history) |
| `courtlistener` | CourtListener search API | exact name only | US court opinions / dockets |

Merge rules: de-duplicate by normalised title (publisher suffix stripped) or URL; order negative →
mention → neutral; keep up to 40 articles but preserve total counts; record a
`AdverseMediaProviderStatus` per source. **Any** source succeeding → `Succeeded=true` with
`Error` naming the failed ones (partial coverage, still counts as checked); **all** failing →
`Succeeded=false` = *media unavailable*, a coverage gap, never "clear".

**Risk lexicon (`AdverseMediaLexicon.Categories`)** — matched whole-word, case-insensitive,
longest term first:

| Category | Terms |
|---|---|
| Financial crime | fraud, fraudulent, money laundering, laundering, embezzlement, embezzled, ponzi, scam, bribery, bribe, kickback, tax evasion, wire fraud, racketeering, extortion, forgery |
| Criminal proceedings | indicted, indictment, arrested, arrest, convicted, conviction, charged with, pleaded guilty, guilty, sentenced, felony, prison, criminal charges |
| Civil / litigation | lawsuit, sued, class action, settlement, judgment against, bankruptcy, insolvency, receivership, liquidation, default judgment |
| Regulatory | sanctions, sanctioned, fined, penalty, enforcement action, investigation, probe, subpoena, cease and desist, consent order, license revoked, banned, deregistered |
| Payments / card risk | chargeback, chargebacks, counterfeit, data breach, skimming, bust-out, shell company, transaction laundering, terminated merchant |
| Organised crime / terrorism | terrorism, terrorist, cartel, trafficking, organized crime, organised crime, smuggling |

`QueryTerms` (the subset sent to search engines): fraud, laundering, indicted, lawsuit, scam,
embezzlement, bribery, sanctions, arrested, convicted, ponzi, chargeback, counterfeit,
investigation, fined, bankruptcy. Grading always uses the full lexicon regardless of the query.

**Grading (`AdverseMediaAnalyzer.Grade`)** — the headline and every sentence of the snippet are
tested for the subject (individuals: full name or first+last token; organisations: all tokens after
stripping `LLC`/`Inc`/`Ltd`/`Corp`/`Co`/… ) and for lexicon terms:

* **negative** — name and a term in the *same sentence or headline*; that sentence becomes `Context`,
  terms → `MatchedTerms`, categories → `Category`.
* **mention** — name and terms in the same article but never the same sentence.
* **neutral** — otherwise.

Flags: `ADVERSE_MEDIA` (always Medium — never High, so media alone cannot push `KybRisk` to High or block auto-approve)
with terms, categories, sources and the quoted lead sentence; `ADVERSE_MEDIA_MENTION` (Low). The
evidence is carried as `AdverseMediaEvidence` into the assessment brief: check-outcome detail,
per-subject narrative, the **Adverse media evidence** table in the PDF and Explainability tab, and
per-source status pills in the KYB tab. Grading is lexical — analysts must confirm the named party
is the applicant and not a namesake.

Licensing: OpenSanctions bulk data is CC BY-NC 4.0 (commercial use needs their licence); OFAC and
UN lists are public domain.

## Website compliance — `Compliance/WebsiteComplianceScanner.cs`

Card-brand website requirements scored 0–100 with grade A–F. Reuses the shared website
`HttpClient` and `HtmlTextExtractor`; crawls the homepage plus linked policy pages.

| Check code | What it verifies |
|-----------|------------------|
| `SITE_UNREACHABLE`, `TLS` | Reachable; served over HTTPS |
| `PRIVACY_POLICY`, `TERMS_CONDITIONS`, `REFUND_POLICY`, `DELIVERY_POLICY` | Dedicated page found and substantive (Pass), link/wording only (Warn), absent (Fail) |
| `CUSTOMER_SERVICE_CONTACT` | Email / phone / address present |
| `CURRENCY_DISCLOSURE`, `CARD_ACCEPTANCE_MARKS`, `CHECKOUT_PRESENT`, `EXPORT_RESTRICTIONS` | Commerce disclosures |
| `LEGAL_NAME_DISCLOSED` | Declared legal name appears on the site |
| `PLACEHOLDER_CONTENT` | Not an under-construction / parked page |
| `PROHIBITED_CONTENT` / category flags | Site text run through the prohibited-business detector |
| `DOMAIN_AGE`, `DOMAIN_EXPIRING` | RDAP registration date and expiry |

Limitations: static HTML only (no JavaScript rendering — SPA sites can look empty), robots.txt is
not consulted, 15 s per request, 4 MB cap.

## Prohibited & restricted business — `Prohibited/ProhibitedBusinessDetector.cs`

`Analyze(websiteText, businessDescription, declaredMcc)` classifies against 23 categories in
`Resources/restricted-categories.json` (CBD/cannabis, adult, gambling, firearms, tobacco/vape,
crypto, debt collection, MLM, pharma, nutraceuticals, weapons, …), each with keyword/phrase
patterns, an MCC list and a policy (`Acceptable` / `HighRisk` / `Restricted` / `Prohibited`).

Scoring per category:

1. Website text and description are scanned; multi-word phrases weigh 1.5×.
2. A keyword that also appears in the **description** counts **2×** — self-declared evidence beats
   incidental site text.
3. Density matters (hits per words of text), breadth (distinct keywords) beats repetition; normalised 0–1.
4. Declared MCC in the category's MCC list → score × 1.5 and the match is kept even when weak.
5. Matches ≥ 0.35 (or MCC-in-category) raise a flag; verdict = worst policy among flags.

`Prohibited` → `PROHIBITED_BUSINESS` hard stop; `Restricted` → High reason → Refer. The
description is what gives coverage when the merchant has no website; the MCC alone never clears
or condemns.

## Combined report

`KybReportService` (`POST /api/kyb/report`) runs verification, screening, website compliance and
prohibited-business for one applicant and returns an overall `RiskTier` with the merged flag list —
the same services the KYB and Pre-check agents call inside a full assessment.
