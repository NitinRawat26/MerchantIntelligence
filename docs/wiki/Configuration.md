# Configuration

Standard ASP.NET Core configuration: `appsettings.json` → `appsettings.{Environment}.json` →
user secrets → environment variables (`Section__Key`). **Never commit keys**; use
`dotnet user-secrets set "Kyb:FoursquareApiKey" "<key>" --project src/MerchantIntelligence.Api`
locally and environment variables on the host.

## Settings

| Key | Default | Purpose |
|-----|---------|---------|
| `CreditDecision:ModelPath` | `models/credit-decision.zip` | Credit model; the API refuses to start if missing (run the Trainer) |
| `MccValidation:ModelPath` | `models/mcc-classifier.zip` | MCC text classifier; if missing the provider is *unavailable* and the rest runs |
| `MccValidation:FilerIndexPath` | `models/filer-domains.json` | SEC filer domain → SIC snapshot |
| `Kyb:UserAgent` | `MerchantIntelligence/1.0 (+repo url)` | Sent to GLEIF/EDGAR/OSM; SEC requires contact details here |
| `Kyb:OpenCorporatesApiToken` | null | Enables OpenCorporates registry provider |
| `Kyb:CompaniesHouseApiKey` | null | Enables UK Companies House provider |
| `Kyb:FoursquareApiKey` | null | Enables Foursquare Places in local presence |
| `Kyb:GooglePlacesApiKey` | null | Enables Google Places in local presence |
| `Kyb:LocalPresenceEnabled` | `true` | Turns the local-presence lookup on/off |
| `Kyb:LocalPresenceRadiusMeters` / `Kyb:LocalPresenceLocalityRadiusMeters` | `250` / `5000` | POI search radius around the geocoded address / around the locality fallback |
| `Kyb:MaxResultsPerSource` | `5` | Registry records kept per provider |
| `Kyb:NewEntityThresholdMonths` | `12` | Age below which `NEW_ENTITY` is flagged |
| `Sanctions:CacheDirectory` | `data/sanctions` | Downloaded list cache (~100 MB) |
| `Sanctions:RefreshInterval` | `1.00:00:00` | Re-download interval |
| `Sanctions:MatchThreshold` | `0.85` | Fuzzy name-match threshold |
| `Sanctions:IncludePeps` | `false` | Also load the large OpenSanctions PEP dataset |
| `Sanctions:IncludeRawGovernmentLists` | `true` | Load OFAC SDN and UN XML in addition to OpenSanctions |
| `Sanctions:EnableAdverseMedia` | `true` | GDELT adverse-media search |
| `Platform:DatabasePath` | `data/platform.db` | SQLite file; `:memory:` for tests |
| `Platform:ModelsDirectory` | `models` | Where model ops writes retrained/promoted models |
| `Match:Endpoint`, `Match:ApiKey` | null | MATCH-compatible service; otherwise `NotConfigured` |
| `Match:LocalListPath` | null | CSV of your own terminated merchants used as a local MATCH list |
| `Cors:AllowedOrigins[]` | `http://localhost:4200` | Extra UI origins |
| `Logging:*` | Information | Standard logging |

Paths are resolved relative to the content root (`ResolvePath` in `Program.cs`), so the same
defaults work from the repo root and inside the container.

Environment-variable form: `Kyb__FoursquareApiKey=<key> dotnet run --project src/MerchantIntelligence.Api`.

## Optional keys — where to get them

| Setting | Source |
|---------|--------|
| `Kyb:FoursquareApiKey` | <https://foursquare.com/developers/> (Places API service key) |
| `Kyb:GooglePlacesApiKey` | Google Cloud console, Places API enabled |
| `Kyb:OpenCorporatesApiToken` | <https://opencorporates.com/api_accounts/new> |
| `Kyb:CompaniesHouseApiKey` | <https://developer.company-information.service.gov.uk/> |

When a key is absent the source is reported as *Not configured* in results and skipped; nothing fails.

## Docker

```bash
docker build -t merchant-intelligence .
docker run --rm -p 8080:8080 merchant-intelligence      # UI, /swagger, /health on :8080
```

The multi-stage `Dockerfile` builds the Angular bundle and the API into one image; the API listens on
`$PORT` (default 8080) and serves the SPA from `wwwroot`.

## Render

`render.yaml` is a Render Blueprint (free Docker web service, branch `base`, health check
`/health`, auto-deploy). Env vars set there: `ASPNETCORE_ENVIRONMENT=Production`, `Kyb__UserAgent`,
`Sanctions__CacheDirectory=/app/data/sanctions`, `Platform__DatabasePath=/app/data/platform.db`.

Free-tier caveats:

* sleeps after ~15 min idle; first request afterwards takes 30–60 s;
* no persistent disk → SQLite (workflows, rules, cases, audit, assessments) and the sanctions cache
  reset on every deploy/restart — attach a disk or point at an external store for durability;
* 512 MB RAM; ~50 MB idle, ~170 MB with the MCC classifier loaded.

Add secrets (`Kyb__*`, `Match__*`) as service environment variables in the Render dashboard, never
in the blueprint file.
