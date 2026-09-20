# Step 5 · `screening` — Sanctions, PEP and adverse-media screening

| | |
|---|---|
| Step id | `screening` |
| Agent | KYB (`kyb`) |
| Stage | 1, runs in parallel with `presence` and `match` after `verification` |
| Depends on | `verification` (ordering only — the inputs come from intake; the dependency lets the KYB agent re-screen the registry alias afterwards) |
| Parameters | `includeTradingName` (default `true`), `includeOwners` (default `true`) |
| Can hard-stop | **Yes** — `SANCTIONS_MATCH` |
| Consumed by | KYB agent review, `KybRisk` roll-up (`terms`, `score`), Screening score component (15 %), rules `HARD_STOP_SANCTIONS` / `PEP_EDD`, stop-gates, analyst brief |
| Implementation | `src/MerchantIntelligence.Kyb/Sanctions/SanctionsScreeningService.cs`, `SanctionsIndex.cs`, `SanctionsListSources.cs`; wrapper `src/MerchantIntelligence.Platform/Agents/Kyb/ScreeningStep.cs` |

---

## 1. Why this step exists

### Functional purpose
Check the business, its trading name and every declared beneficial owner / principal against consolidated **sanctions lists**, optionally **PEP** (politically exposed person) data, and recent **negative news**, producing fuzzy-matched hits with an explanation of why each hit scored as it did.

### Business question answered
*"Is anyone we would be doing business with — the company or the people behind it — someone we are legally forbidden to serve, someone who requires enhanced due diligence, or someone currently in the news for fraud, laundering or similar?"*

### Compliance relevance
* **Sanctions** (OFAC, UN, EU, UK HMT …) are strict-liability: an acquirer that settles funds to a designated person or an entity they own ≥ 50 % commits a violation regardless of intent. This is why a confirmed match is a **hard stop** that no score can override.
* **PEPs** are not prohibited, but AML regulation (FinCEN CDD, FATF Recommendation 12, EU AMLD) requires enhanced due diligence — senior sign-off, source-of-wealth checks, ongoing monitoring. The system therefore *refers* rather than declines.
* **Adverse media** is a recognised risk indicator in FATF guidance and card-brand underwriting standards; it is advisory and always needs human reading of the articles.

### KYB vs KYC
This is the step where the **KYC of the people** happens: each `BeneficialOwner` is screened as an individual with DOB and nationality. The entity (KYB) is screened as an organisation. Both live in one report so the acquirer's file shows entity and owner screening side by side, as examiners expect.

### Fraud relevance
Names on sanctions/PEP lists are rarely used by fraudsters, but the GDELT adverse-media search surfaces recent lawsuits, indictments, chargeback scandals and "scam" coverage that never reach a formal list.

---

## 2. Inputs

### Subjects built by `ScreeningStep`

| Subject | Built from | `IsIndividual` | Role label |
|---|---|---|---|
| Legal name | `Business.LegalName`, `Business.Country` | false | `Business` |
| Trading name (if differs and `includeTradingName`) | `Business.TradingName` | false | `Trading name` |
| Each owner (if `includeOwners`) | `Owner.FullName`, `DateOfBirth`, `Nationality`, `Role` | true | owner's role or `Beneficial owner` |
| Registry alias (added later by the KYB **agent**) | `Verification.BestMatch.Record.LegalName` when it differs from both declared names | false | `Registry alias (<source>)` |

`ScreeningSubject(Name, DateOfBirth?, Country?, IsIndividual, Role?)` — DOB and country are **corroborators**; they are optional but they are what turns a possible name match into a confirmed one, so complete owner data materially improves precision.

### Lists (`ISanctionsListSource`) — all free, no keys

| List | Enabled | Source | Notes |
|---|---|---|---|
| **OpenSanctions `sanctions`** | always | `data.opensanctions.org/datasets/latest/sanctions/targets.simple.csv` | Consolidation of OFAC, EU, UK, UN, and ~100 other programmes with aliases, DOBs, countries |
| **OFAC SDN** (raw) | `Sanctions:IncludeRawGovernmentLists` = `true` (default) | `sanctionslistservice.ofac.treas.gov/…/SDN.CSV` | Authoritative US list, parsed directly |
| **UN Security Council consolidated** (raw) | same option | `scsanctions.un.org/resources/xml/en/consolidated.xml` | Individuals and entities |
| **OpenSanctions `peps`** | `Sanctions:IncludePeps` = **`false` by default** | same host, `peps` dataset | ~180 MB download, hundreds of MB RAM — `PEP_MATCH` can only fire when this is on |

Lists are downloaded to `Sanctions:CacheDirectory` (default `data/sanctions`), reused for `RefreshInterval` (24 h), and rebuilt into an in-memory `SanctionsIndex`. Each list's status (`ListName`, `EntityCount`, `LoadedAt`, `Error`) is recorded in the report so the analyst knows what the subjects were screened *against*.

### Adverse media (`IAdverseMediaProvider` → `CompositeAdverseMediaProvider`)
Enabled by `Sanctions:EnableAdverseMedia` (default `true`); the free, keyless sources in `Sanctions:AdverseMediaSources` are queried **in parallel per subject** and merged:

| Source | Endpoint | What it contributes | Rate handling |
|---|---|---|---|
| `gdelt` | GDELT DOC 2.0 (`timespan=3months`, `maxrecords=25`) | global news index, titles + tone | one request per 5.2 s process-wide (shared gate), one retry after HTTP 429 |
| `googlenews` | Google News RSS (`"<name>" (fraud OR indicted OR …)`) | headlines + description snippets, publisher stripped from title | none needed |
| `bingnews` | Bing News RSS | headlines + snippets | none needed |
| `wikipedia` | MediaWiki search API | encyclopaedic snippets (legal history, controversies) | none needed |
| `courtlistener` | CourtListener search API | US court opinions/dockets naming the subject | none needed |

#### Risk lexicon (`AdverseMediaLexicon`)
The complete set of words and phrases searched for in every headline, snippet and record, grouped by the risk category they denote. Matching is **whole-word, case-insensitive, culture-invariant** (`\bterm\b`), so "fraud" does not match "defrauded" but "wire fraud" is caught as its own multi-word term; longer terms are tested first so "money laundering" is reported once, not as both "money laundering" and "laundering".

| Category | Terms | Why it matters to an acquirer |
|---|---|---|
| **Financial crime** | fraud, fraudulent, money laundering, laundering, embezzlement, embezzled, ponzi, scam, bribery, bribe, kickback, tax evasion, wire fraud, racketeering, extortion, forgery | Direct predictors of merchant fraud, bust-out and AML exposure |
| **Criminal proceedings** | indicted, indictment, arrested, arrest, convicted, conviction, charged with, pleaded guilty, guilty, sentenced, felony, prison, criminal charges | Shows a matter has reached prosecution stage – far stronger than an allegation |
| **Civil / litigation** | lawsuit, sued, class action, settlement, judgment against, bankruptcy, insolvency, receivership, liquidation, default judgment | Solvency and reputational risk; bankruptcy/receivership bear directly on reserve sizing |
| **Regulatory** | sanctions, sanctioned, fined, penalty, enforcement action, investigation, probe, subpoena, cease and desist, consent order, license revoked, banned, deregistered | Regulator action against the business or a principal; licence loss can make the MCC unboardable |
| **Payments / card risk** | chargeback, chargebacks, counterfeit, data breach, skimming, bust-out, shell company, transaction laundering, terminated merchant | Card-scheme specific: MATCH-type behaviour, PCI incidents, factoring/transaction laundering |
| **Organised crime / terrorism** | terrorism, terrorist, cartel, trafficking, organized crime, organised crime, smuggling | Highest-concern category; still Medium severity, surfaced prominently for analyst review |

**Query terms.** A compact 16-word subset (`QueryTerms`) is what is actually sent to the search engines so the result set is already biased toward risk coverage: `fraud, laundering, indicted, lawsuit, scam, embezzlement, bribery, sanctions, arrested, convicted, ponzi, chargeback, counterfeit, investigation, fined, bankruptcy`. Per source: GDELT and Google News use all 16 (`"<name>" (fraud OR laundering OR …)`), Bing News the first 8, Wikipedia the first 10, CourtListener searches the exact name only (court records are inherently adverse). Grading afterwards always uses the **full lexicon**, so a term that was not in the query (e.g. "receivership") is still detected in the returned text.

**Subject matching inside a sentence.** For individuals the sentence must contain the full name, or the first **and** last name token (middle names/initials are often dropped by the press). For organisations, legal suffixes and stop-words (`LLC`, `Inc`, `Ltd`, `Corp`, `Co`, `Company`, `Limited`, `PLC`, `GmbH`, `Holdings`, `Group`, `the`, `and`, `of`, …) are stripped and every remaining token must appear, so "Blue Ocean Bakery was sued" matches subject "Blue Ocean Bakery LLC". HTML tags are removed and entities decoded before matching. Sentence boundaries are `.`, `!`, `?` followed by a capital/quote, and line breaks; the headline is treated as its own sentence.

Every article is graded by `AdverseMediaAnalyzer` against that lexicon:

* **negative** – the subject's name and a risk term occur in the **same sentence or headline**; the sentence is kept as `Context` and the terms as `MatchedTerms` / `Category`.
* **mention** – name and risk terms occur in the article/snippet but never in the same sentence (softer signal, `ADVERSE_MEDIA_MENTION`, Low).
* **neutral** – no name–term relationship.

Stories seen in several sources are de-duplicated on normalised title/URL; negatives sort first and the top 40 are kept (counts stay whole). Each source's outcome is recorded in `AdverseMediaResult.Providers`; the search is `Succeeded` when **any** source answered, with the failed ones named in `Error` ("1 of 5 source(s) unavailable: GDELT …"). Only when *every* source fails is the result `Succeeded=false` — and that is a coverage gap, never zero articles.

---

## 3. Internal flow

```mermaid
flowchart TD
    A[ScreeningStep builds subjects<br/>legal name · trading name · owners] --> B[ScreenAsync]
    B --> L{Index fresh?<br/>< RefreshInterval}
    L -- no --> D[Download / reuse cached CSV+XML per list<br/>parse → SanctionsIndex; record ListStatus incl. errors]
    L -- yes --> E
    D --> E[for each subject]
    E --> F[Index.Search subject, threshold 0.85]
    F --> F1[Tokenise name; inverted-index votes<br/>exact token +2, 3-char prefix +1]
    F1 --> F2[Candidates with votes ≥ max 2, tokenCount]
    F2 --> F3[NameMatcher.Similarity vs every alias → bestScore]
    F3 --> F4{bestScore ≥ 0.75?}
    F4 -- no --> E
    F4 -- yes --> F5[Adjust: type mismatch −0.10<br/>birth-year match +0.08 / mismatch −0.15<br/>country match +0.05 / mismatch −0.05]
    F5 --> F6{score ≥ 0.85 → hit}
    F6 --> G[Composite adverse-media search<br/>GDELT gated 5 s · Google News · Bing News · Wikipedia · CourtListener<br/>grade: name + risk term in same sentence → negative<br/>all sources fail → Succeeded=false]
    G --> H{hits?}
    H -- yes --> H1{top ≥ 0.95, or ≥ 0.90 with<br/>DOB / country corroboration}
    H1 -- yes --> S1[SANCTIONS_MATCH High]
    H1 -- no --> S2[SANCTIONS_POSSIBLE_MATCH Medium]
    H -- yes --> P{any hit from a PEP list / programme?}
    P -- yes --> S3[PEP_MATCH Medium]
    G --> M{media Succeeded and NegativeCount > 0}
    M -- yes --> S4[ADVERSE_MEDIA with terms, category,<br/>source, date and quoted sentence<br/>≥3 negative or organised crime → High else Medium]
    G --> M2{MentionCount > 0}
    M2 -- yes --> S5[ADVERSE_MEDIA_MENTION Low]
    S1 & S2 & S3 & S4 & S5 --> R[SubjectScreeningResult]
    R --> O[OverallRisk = max flag severity, Low if none<br/>ScreeningReport → ctx.Screening]
    O --> AG[KYB agent: registry alias re-screened<br/>and merged into the report]
```

### 3.1 Matching detail
* Names are normalised (diacritics, punctuation, case) and tokenised **without** stripping legal suffixes — for sanctions, "Ltd" vs "LLC" is real signal.
* The inverted index (exact-token and 3-character-prefix postings) keeps the search fast over hundreds of thousands of entities; only candidates with enough "votes" are fuzzily scored.
* `NameMatcher.Similarity` is the same token-set + Jaro-Winkler blend used in `verification` (see that page, §3.1); it is robust to word order, initials and transliteration variants.
* Every hit carries `Reasons` — human-readable lines like *"Name similarity 92 % vs 'Viktor Anatolyevich BOUT'"*, *"Birth year 1967 matches listing"*, *"Country US differs from listing (RU)"*. These are what the analyst uses to disposition.
* Up to 10 hits per subject, best first.

### 3.2 Confirmed vs possible
A **confirmed** match (`SANCTIONS_MATCH`) requires either a near-exact name (≥ 0.95) or a strong name (≥ 0.90) plus at least one corroborating attribute (birth year or country *matches listing*). Anything else above 0.85 is `SANCTIONS_POSSIBLE_MATCH`. This asymmetry is deliberate: a false hard stop on a common name would wrongly decline a legitimate merchant, while a possible match still forces analyst review through the Medium severity and KybRisk.

---

## 4. Outputs

```csharp
ScreeningReport(
    IReadOnlyList<SubjectScreeningResult> Subjects,   // per subject: PotentialMatch, Hits, AdverseMedia, Flags
    IReadOnlyList<SanctionsListStatus> Lists,         // what was loaded, how many entities, errors
    RiskTier OverallRisk,                             // max flag severity; Low when no flags
    IReadOnlyList<KybScreeningFlag> Flags)
```

| Flag | Severity | Trigger |
|---|---|---|
| `SANCTIONS_MATCH` | High | confirmed match (see §3.2) — **hard stop** |
| `SANCTIONS_POSSIBLE_MATCH` | Medium | hit(s) ≥ 0.85 not meeting confirmation criteria |
| `PEP_MATCH` | Medium | any hit whose list name contains "peps" or programme contains "PEP" |
| `ADVERSE_MEDIA` | Medium (never High — lexical media matching is too noisy to drive KYB risk to High on its own) | articles/records where the name and a risk term share a sentence or headline. Message lists the matched terms, categories, contributing sources, and quotes the lead sentence with publisher and date |
| `ADVERSE_MEDIA_MENTION` | Low | name and risk terms present in the same article but never in the same sentence – analyst to confirm relevance |

Each `SubjectScreeningResult.AdverseMedia` carries: `ArticleCount`, `NegativeCount`, `MentionCount`, up to 40 `Articles` (each with `Title`, `Url`, `Source`, `Published`, `Tone`, `Snippet`, `MatchedTerms`, `Category`, `Context`, `Provider`), `Providers` (per-source succeeded/count/error) and `Error` naming any source that failed.

Timeline summary: `4 subject(s) screened · 1 potential match(es) · 2 adverse-media article(s) · overall Medium` (suffixed `(media unavailable)` when every source failed for a subject).

### Where the evidence surfaces

| Surface | Content |
|---|---|
| Check outcome *Sanctions / PEP / media* (brief, PDF table, UI) | result `Clear` / `Clear · media mentions` / `Adverse media` / `Lists clear · media unavailable`; detail line `Adverse media: N negative, M indirect mention(s) of T article(s)/record(s) from Google News, Wikipedia … Source(s) unavailable: GDELT …` |
| Detailed narrative | one paragraph per subject with negatives: term frequency (`fraud ×3, indicted ×1`) and up to three quoted sentences with publisher, date and source |
| **Adverse media evidence** section (PDF and Explainability tab) | table of every negative/mention item: subject, tone, title, publisher · date · via source, quoted sentence, matched terms + category, URL |
| All findings | the `ADVERSE_MEDIA` / `ADVERSE_MEDIA_MENTION` flag messages as reason codes |
| Recommended analyst actions | "confirm the named party is this applicant (not a namesake)" for negatives; spot-check for mentions; re-run when media unavailable |
| KYB tab, per subject | source pills (count or *unavailable*), then each article with tone pill, risk terms, category and quoted context |

---

## 5. Downstream impact

```mermaid
flowchart LR
    S[ctx.Screening] --> LL{ListsLoaded?<br/>any list with EntityCount>0 and no error}
    LL -- no --> N["SanctionsMatch = PepMatch = AdverseMedia = null<br/>Screening component UNCOVERED"]
    LL -- yes --> SM[SanctionsMatch / PepMatch booleans]
    LL -- yes --> MC{MediaChecked?<br/>every subject had at least one<br/>media source answer}
    MC -- no --> AN[AdverseMedia = null]
    MC -- yes --> AM[AdverseMedia boolean]
    SM & AM --> SC[score: Screening component 15 %]
    S --> HS["ctx.HardStop = SANCTIONS_MATCH<br/>(only when lists loaded)"]
    HS --> R1[rules HARD_STOP_SANCTIONS → Decline]
    HS --> SG[stop-gates · haltOnHardStop]
    S --> KR["KybRisk = max(...)" ] --> T[terms risk adjustment]
    S --> SIG[CollectSignals: every flag → reason code]
    SIG --> R2[rules PEP_EDD → Refer]
    S --> AG[KYB agent findings<br/>SANCTIONS_MATCH · PEP_EDD · ALIAS_RESCREENED]
```

### Unified score — `Screening` component (weight 0.15)

```text
start 100
SanctionsMatch  → 0, hard stop SANCTIONS_MATCH (High)   → unified score capped at 150, Decline
PepMatch        → −40, reason PEP_MATCH (Medium)
AdverseMedia    → −25, reason ADVERSE_MEDIA (Medium)
clamp 0–100
```
`SANCTIONS_POSSIBLE_MATCH` does **not** reduce the component numerically; it reaches the score as a Medium reason code (via `CollectSignals`) and lifts `KybRisk` to Medium, which reduces the *Kyb* component to 55. `ADVERSE_MEDIA` is capped at Medium, so media findings lower the Kyb component to 55 and add a Medium reason code but never trigger the high-severity cap on their own.

### Rules (default set)
* `HARD_STOP_SANCTIONS` (priority 1): `hardStops contains SANCTIONS_MATCH` → **Decline**.
* `PEP_EDD` (priority 30): `reasonCodes contains PEP_MATCH` → **Refer**.

### Workflow control
* `ctx.HardStop` returns `SANCTIONS_MATCH` as soon as the report contains a confirmed match **and lists actually loaded**. With `haltOnHardStop: true` in the workflow definition, all remaining evidence steps are skipped with *"hard stop … already established"*; the default workflow has it `false` so the full picture is still gathered.
* Stop-gates on `screening` may trigger on `HardStop` or on specific flag codes (`RaisesFlags` includes `screening`).

### KYB agent
Re-screens the registry legal name if it differs (see `verification` page) and adds observations `SANCTIONS_MATCH` ("Hard stop – policy declines regardless of score") and `PEP_EDD` ("Enhanced due diligence is required").

---

## 6. Missing input, failures and coverage — read carefully

| Situation | What the report shows | How the score treats it |
|---|---|---|
| No owners declared | Only business (and trading name) screened | Owner KYC is silently absent — analysts should check `ownersDeclared` fact; FinCEN CDD expects ≥ 25 % owners |
| Owner without DOB / nationality | Name-only matching | More `SANCTIONS_POSSIBLE_MATCH`, fewer confirmations — add data and re-run |
| `IncludePeps=false` (default) | No PEP list loaded → `PEP_MATCH` cannot fire | Absence of a PEP flag is **not** evidence of no PEP exposure |
| GDELT rate-limited / fails for any subject | `AdverseMedia.Succeeded=false`, brief says *"Adverse media NOT checked (…) – media result is unknown, not clear"*; outcome severity lifted to Medium | `AdverseMedia=null`; next action *"Re-run adverse-media screening"* |
| Every list download fails / cache empty | `Lists` all with errors, `EntityCount=0`; subjects trivially have zero hits | `ListsLoaded=false` → all three booleans `null`, Screening component **uncovered** (coverage drops ≥ 15 points), no hard stop possible. Never read as "clear". |
| Step throws / times out | `Failed`, `ctx.Screening=null` | Component uncovered; brief "Sanctions / PEP / media: Not run" (High severity outcome, next action *"Re-run sanctions screening before any approval"*) |
| Only trading name differs | Both names screened | Normal |

Analyst rule of thumb: **"Clear"** in the brief means *lists loaded, media checked, no flags*. **"Lists clear · media unavailable"** is a coverage gap.

---

## 7. Analyst interpretation and remediation

| Finding | Read as | Do |
|---|---|---|
| `SANCTIONS_MATCH` | Confirmed designation | Decline; do not onboard; follow internal OFAC escalation (possible blocking/reporting obligations). Open `Entity.SourceUrl` to document. |
| `SANCTIONS_POSSIBLE_MATCH` | Fuzzy name hit, uncorroborated | Read `Reasons`; compare DOB, nationality, programme, listing date; request owner ID document; document the false-positive disposition in the case notes (audit trail). |
| `PEP_MATCH` | Owner (or namesake) is a PEP | Confirm identity; if genuine PEP: EDD — source of wealth/funds, senior approval, ongoing monitoring. Refer outcome is by design. |
| `ADVERSE_MEDIA` Medium | 1–2 negative-title articles | Read them; check whether about *this* entity/person; note in file. |
| `ADVERSE_MEDIA` Medium, many articles | ≥ 3 negatives or organised-crime terms | Treat as material; read the quoted sentences; escalate manually if the articles clearly concern this applicant and fraud/chargebacks. |
| `ALIAS_RESCREENED` with hits | Registry name hit while declared name did not | Strong indicator of deliberate name variation — escalate. |
| Media unavailable | every news/records source down or rate-limited | Re-run before final approval. |

### False positives / negatives
* **Common names** (Mohammed Ali, John Smith, "Global Trading LLC") generate possible matches; DOB and nationality on owners are the main defence. The 0.7 single-token cap in `TokenSetRatio` prevents "Ali" alone from scoring high.
* **Transliteration** (Bout / But / Butt) is handled by Jaro-Winkler on normalised strings, but ordering of patronymics can still push scores under 0.85 → false negative. Where the merchant is high-risk, analysts should search OpenSanctions manually with alternate spellings.
* **Lexicon matching is lexical, not semantic**: a sentence such as "Jane Roe praised the fraud investigation team" still grades negative because name and term share a sentence; the quoted `Context` is surfaced precisely so the analyst can dismiss it. Namesakes are not resolved – the brief instructs the analyst to confirm the named party is the applicant.
* **Ownership-based sanctions (OFAC 50 % rule)** are not evaluated — an entity owned by a sanctioned person but not itself listed will pass unless the owner is declared and screened.
* Screening is point-in-time; lists change daily and there is no post-boarding re-screen in this workflow.

---

## 8. Worked examples

**A. Routine SMB, full data.** Subjects: "Bright Bean Coffee Roasters LLC" (US), owner "Maria Delgado" (1979-04-12, US). Lists: OpenSanctions 62 k, OFAC 12 k, UN 1 k loaded. No candidates pass the vote threshold; GDELT returns 0 articles. Report: 2 subjects, 0 matches, `OverallRisk Low`. Score: Screening component 100 → 15 points; brief "Sanctions / PEP / media: Clear".

**B. Namesake owner.** Owner "Viktor Bout" with DOB 1985, nationality US. Index candidate "Viktor Anatolyevich BOUT" (OFAC, DOB 1967, RU). Name similarity 0.92; type ok; birth year mismatch −0.15 → 0.77; country mismatch −0.05 → 0.72 < 0.85 → **no hit**. Report clear; the DOB did its job.

**C. Same owner, no DOB or nationality supplied.** Score stays 0.92 ≥ 0.85 → hit; 0.92 < 0.95 and no corroboration → `SANCTIONS_POSSIBLE_MATCH` (Medium). KybRisk Medium → Kyb component 55; reason code added; Screening component unchanged at 100; brief "Possible match … need analyst disposition". Analyst obtains ID, records DOB 1985, re-runs → clear.

**D. Confirmed hit.** Business "Rosoboronexport" (RU). Name similarity 1.0, country matches (+0.05) → 1.0 → `SANCTIONS_MATCH` High. `ctx.HardStop=SANCTIONS_MATCH`; Screening component 0; unified score capped at 150, tier VeryHigh; rule `HARD_STOP_SANCTIONS` → Decline. With `haltOnHardStop: true` the Financial and Decision evidence steps would be skipped.

**E. Lists failed to download** (network egress blocked). `Lists` show three errors, 0 entities. All subjects "clear" trivially. `ListsLoaded=false` → Screening **uncovered**, coverage 85 %; brief: "Sanctions / PEP / media: Unavailable" (High severity) with the narrative *"this must not be read as clear"* and next action *"Re-run sanctions screening before any approval"*. Coverage alone (85 %) would not block auto-approve, so the analyst must act on that outcome — the system deliberately does not fabricate a "clear".
