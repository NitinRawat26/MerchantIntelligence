# Data sources

Every external source the suite talks to, what it is used for, whether a key is needed, and the
licence/terms that matter. Nothing here is a language model; all sources return structured data or
HTML that is processed deterministically.

| Source | Used by | Key | Notes / terms |
|--------|---------|-----|---------------|
| **GLEIF LEI API** (`api.gleif.org`) | Registry verification | no | Open data; legal entities with an LEI only (mostly larger firms) |
| **SEC EDGAR** — full-text company search (`efts.sec.gov`), submissions (`data.sec.gov`) | Registry verification (live), MCC filer index (offline snapshot `models/filer-domains.json`), classifier training (`DataPipeline`) | no | Requires a descriptive `User-Agent` with contact details (`Kyb:UserAgent`, `EDGAR_USER_AGENT`); ≤ 10 req/s (client throttles). Public-company coverage only |
| **US Census geocoder** | Address normalisation / geocoding | no | US addresses only |
| **OpenStreetMap Nominatim** | Geocoding fallback | no | Usage policy: 1 req/s, identify with UA; not for bulk |
| **OpenStreetMap Overpass** | Local presence POI search (`amenity=`, `shop=`, `cuisine=`, …) | no | ODbL data; public endpoints are rate-limited and best-effort |
| **Foursquare Places API** | Local presence | `Kyb:FoursquareApiKey` | Free tier available; better US POI coverage than OSM |
| **Google Places API** | Local presence | `Kyb:GooglePlacesApiKey` | Billed per request beyond the free credit |
| **OpenCorporates** | Registry verification | `Kyb:OpenCorporatesApiToken` | Free tier for non-commercial use |
| **UK Companies House** | Registry verification (UK) | `Kyb:CompaniesHouseApiKey` | Free key |
| **Kentucky Secretary of State** business search (`sosbes.sos.ky.gov`) | Registry verification (US-KY applicants; standing, industry, employee band, annual report, registered agent, assumed names) | no (`Kyb:StateRegistriesEnabled`) | Public ASP.NET search form, no API; one search + one profile fetch per query, polite fallbacks by name token |
| **OpenSanctions** `targets.simple.csv` (+ PEP dataset) | Sanctions/PEP screening | no | **CC BY-NC 4.0** — commercial use needs an OpenSanctions licence |
| **OFAC SDN** CSV | Sanctions screening | no | Public domain |
| **UN Security Council consolidated list** XML | Sanctions screening | no | Public domain |
| **GDELT** DOC 2.0 | Adverse-media search (news, last 3 months, tone) | no | `Sanctions:AdverseMediaSources` `gdelt`; ~1 request / 5 s per IP — calls serialised with 5.2 s gap + one retry on 429 |
| **Google News RSS** | Adverse-media search (headlines + snippets) | no | `googlenews`; unlimited, primary fallback when GDELT throttles |
| **Bing News RSS** | Adverse-media search (headlines + snippets) | no | `bingnews` |
| **Wikipedia** search API | Adverse-media (encyclopaedic controversies / legal history) | no | `wikipedia`; CC BY-SA content, snippets only |
| **CourtListener** search API | Adverse-media (US court opinions and dockets naming the subject) | no | `courtlistener`; Free Law Project, public domain records |

Adverse-media sources run in parallel per subject; results are merged, de-duplicated and graded against the risk lexicon (see [KYB and Compliance](KYB-and-Compliance#adverse-media--compositeadversemediaprovider)). Any source succeeding counts as coverage with the failed ones named; all failing is *media unavailable*, never clear.
| **RDAP** registries | Website compliance (domain age / expiry); local presence digital footprint (contact e-mail domain age) | no | Availability varies per TLD |
| **Merchant websites** | Website compliance, MCC validation, prohibited-business text | no | Plain HTTP GET, 15 s, 4 MB, UA `MerchantIntelligenceBot/1.0`; robots.txt not consulted; JavaScript not executed |
| **Mastercard MATCH** | MATCH inquiry | acquirer credentials (`Match:Endpoint`, `Match:ApiKey`) or `Match:LocalListPath` | Without either, result is `NotConfigured` / `found: null` — unknown, never clear |

## Embedded reference data (in the repo)

| File | Content |
|------|---------|
| `MccValidation/Resources/mcc-catalog.json` | MCC catalog with categories, risk tier, keywords |
| `MccValidation/Resources/sic-to-mcc.json` | Curated SIC → MCC crosswalk (weak labels) |
| `Kyb/Resources/restricted-categories.json` | 23 prohibited / restricted / high-risk categories with keywords, MCCs, policy |
| `Underwriting/Resources/industry-benchmarks.json` | Per-MCC ticket ranges, revenue per employee, chargeback rate, delivery days |
| `Platform/Resources/default-rules.json` | Default policy rule set |
| `Platform/Resources/default-workflow.json` | Default assessment workflow |
| `models/credit-decision.zip` | LightGBM credit model (trained on synthetic data) |
| `models/mcc-classifier.zip` | ML.NET TF-IDF text classifier (EDGAR-trained) |
| `models/filer-domains.json` | SEC filer domain → SIC snapshot |

## Interpreting absence

A recurring rule across sources: **not found ≠ clear**.

* Registry miss → `NotFound` (SMBs often have no LEI/EDGAR record → see local presence).
* Filer-domain snapshot miss → "not in snapshot", not "not a filer".
* Sanctions lists not loaded → `Unavailable`, excluded from the score as a coverage gap.
* MATCH not configured → `NotConfigured`, `found: null`.
* OSM-only presence miss → `LOCAL_PRESENCE_NOT_FOUND` at Low severity.

Coverage gaps lower the unified score's certainty (drift toward 500) and can trigger
`LOW_COVERAGE_REFER`; they are listed on the result, the agents' findings and the PDF.
