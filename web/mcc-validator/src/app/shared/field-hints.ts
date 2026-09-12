/**
 * Per-field explanation of exactly where each intake value feeds the decision.
 * Weights quoted are the unified-score component weights (CreditModel 30%, KYB 20%,
 * Screening 15%, BusinessPolicy 10%, WebsiteCompliance 10%, VolumePlausibility 10%,
 * Pricing 5%) and the ReservePricingRecommender / VolumePlausibilityAnalyzer adjustments.
 */
export const FIELD_HINTS: Record<string, string> = {
  legalName:
    'Business identity verification: fuzzy-matched against GLEIF / SEC EDGAR / registry records (name similarity drives the confidence %, <50% → NO_REGISTRY_MATCH).\n' +
    'Screening: screened as an organisation against OFAC / UN / EU / OpenSanctions lists.\n' +
    'MATCH / TMF inquiry, website scan (company-name match) and case title.\n' +
    'Feeds unified score via KYB (20%) and Screening (15%) components; a sanctions hit is a hard stop.',
  tradingName:
    'Business identity verification: an alternative name for the registry match (scored at 95% of a legal-name match).\n' +
    'Screening: screened as a second organisation subject when it differs from the legal name.\n' +
    'MATCH / TMF inquiry (DBA field).',
  registrationNumber:
    'Business identity verification: compared with the registry record. An exact match strongly raises confidence; a mismatch raises REGISTRATION_NUMBER_MISMATCH (High) → KYB risk High → KYB component (20%) scores 20/100.',
  taxId:
    'Only sent to the MATCH / terminated-merchant inquiry (not run without MATCH credentials or a local list). Not used by the credit model or scoring.',
  addressLine:
    'Business identity verification: compared with the registry address (<50% similarity → REGISTERED_ADDRESS_MISMATCH), geocoded via the Census geocoder (US), and checked for virtual-office / mail-drop patterns.\n' +
    'MATCH / TMF inquiry.\n' +
    'Affects KYB risk tier → KYB component (20%).',
  city:
    'Business identity verification: part of the address geocode / registry address comparison.\n' +
    'MATCH / TMF inquiry.',
  region:
    'Business identity verification: part of the address geocode / registry address comparison.\n' +
    'MATCH / TMF inquiry.',
  postalCode:
    'Business identity verification: part of the address geocode / registry address comparison.\n' +
    'MATCH / TMF inquiry.',
  country:
    'Business identity verification: registry jurisdiction must agree (JURISDICTION_MISMATCH otherwise).\n' +
    'Screening: ±5% on every list-match score when the listing country agrees / disagrees.\n' +
    'MATCH / TMF inquiry. Recorded on the unified score signals.',
  websiteUrl:
    'Website compliance scan: refund/privacy/ToS pages, contact info, SSL, domain age (RDAP) → 0–100 score = WebsiteCompliance component (10%); <60 raises WEBSITE_NON_COMPLIANT and adds up to +0.15 pricing risk.\n' +
    'Prohibited-business check: page content is scanned for restricted keywords.\n' +
    'MCC validation: site content is classified and compared with the declared MCC.\n' +
    'Left blank → website, MCC checks are skipped and become coverage gaps.',
  businessDescription:
    'Prohibited & restricted business check: keyword / category classification → verdict (Prohibited = hard stop, HighRisk = 60/100) on the BusinessPolicy component (10%).\n' +
    'Website compliance scan: cross-checked against page content.',
  merchantCategoryCode:
    'Credit model: direct input feature.\n' +
    'Volume plausibility: selects the industry benchmark (ticket p10–p90, revenue per employee).\n' +
    'Reserve & pricing: MCC risk tier adds +0.05 (Medium) / +0.15 (High) risk.\n' +
    'MCC validation: compared with what the website evidence suggests (mismatch → Refer).\n' +
    'Prohibited-business check uses it as a category hint.',
  annualVolume:
    'Credit model: direct input feature (30% component).\n' +
    'Volume plausibility (10% component): implied transactions/day, volume per employee vs benchmark, vs years in business, vs prior-year revenue, vs bank-statement card deposits, vs catalogue size; exact multiples of $1M flagged ROUND_NUMBER_DECLARATION.\n' +
    'Reserve & pricing: exposure = daily volume × delivery days + expected chargebacks; <$100k adds +15 bps, >$10M lowers markup; sets monthly processing cap.\n' +
    'P&L analysis: compared with statement revenue.',
  averageTicket:
    'Credit model: direct input feature (30% component).\n' +
    'Volume plausibility (10% component): compared with the MCC benchmark p10–p90 (above p90 −10, >3× p90 −25 TICKET_FAR_ABOVE_INDUSTRY); volume ÷ ticket gives implied transactions (<24/yr −15).\n' +
    'Reserve & pricing: tickets <$15 get the $0.05 per-transaction fee; used as the denominator for the highest/average spread.',
  highestTicket:
    'Credit model: direct input feature (30% component).\n' +
    'Volume plausibility: highest ÷ average ratio (>25× irregular, >50× EXTREME_TICKET_SPREAD −10).\n' +
    'Reserve & pricing: added to the exposure estimate and sets the single-transaction cap (1.1–1.5×).',
  deliveryDays:
    'Reserve & pricing only: fulfilment windows ≥30 days add up to +0.20 risk (FUTURE_DELIVERY) and extend the exposure window used for the rolling reserve. Blank → industry default for the MCC.',
  cardNotPresentShare:
    'Reserve & pricing only: shares above 50% add 0.05 × share to pricing risk (CARD_NOT_PRESENT). The resulting risk band feeds the Pricing component (5%).',
  offersSubscriptions:
    'Reserve & pricing: +0.05 risk (RECURRING_BILLING) for cancellation disputes → affects risk band, reserve % and Pricing component (5%).',
  offersFreeTrials:
    'Reserve & pricing: +0.10 risk (RECURRING_BILLING, overrides the subscription +0.05) for "unrecognised charge" disputes → risk band, reserve % and Pricing component (5%).\n' +
    'Note: the prohibited-business check detects free-trial offers from the description / website text, not from this flag.',
  existingRelationship:
    'Credit model: direct input feature (30% component).\n' +
    'Reserve & pricing: −0.10 risk (EXISTING_RELATIONSHIP) → better band, lower reserve.',
  employeeCount:
    'Volume plausibility only: card volume per employee vs MCC benchmark (above p90 −12, >2× p90 −30 VOLUME_EXCEEDS_HEADCOUNT_CAPACITY; below p10/4 −12 HEADCOUNT_HIGH_FOR_VOLUME, below p10/20 −30 HEADCOUNT_IMPLAUSIBLE_FOR_VOLUME).\n' +
    'Headcount above the MCC ceiling (e.g. 400 for a single restaurant, scaled by Locations) −25 HEADCOUNT_ABOVE_INDUSTRY_CEILING. Blank → skipped (no penalty).',
  locationCount:
    'Volume plausibility only: raises the headcount ceiling to locations × per-location max for the MCC (e.g. 120 per restaurant) and flags employees per location above that max (−12 HEADCOUNT_HIGH_FOR_LOCATIONS). Blank → single entity assumed.',
  yearsInBusiness:
    'Volume plausibility only: <1 year with large volume −25 (STARTUP_WITH_LARGE_VOLUME), <2 years −12. Blank → skipped.',
  priorYearRevenue:
    'Volume plausibility only: declared card volume vs last year\'s revenue (>1.5× growth −12, above total revenue −30 VOLUME_EXCEEDS_REVENUE). Blank → taken from the P&L statement revenue when one is supplied.',
  websiteProductCount:
    'Volume plausibility only: for online-only merchants (Physical location = No / Unknown) a thin catalogue with large volume is flagged THIN_CATALOGUE_LARGE_VOLUME (−20).',
  hasPhysicalLocation:
    'Volume plausibility only: "Yes" disables the thin-catalogue check; "No / Unknown" keeps it active.',
  bankStatement:
    'Bank statement cash-flow analysis: monthly inflows, card deposits, NSF/overdrafts, negative-balance days, volatility → findings.\n' +
    'Volume plausibility: annualised card deposits vs declared volume (>2× −30 DECLARED_FAR_ABOVE_STATEMENTS, far below → possible volume splitting).\n' +
    'Optional: blank = check skipped and listed as a coverage gap.',
  financialStatement:
    'P&L / balance-sheet analysis: margins, leverage, liquidity ratios → findings; statement revenue is compared with declared volume and fills Prior-year revenue when blank.\n' +
    'Optional: blank = check skipped and listed as a coverage gap.',
  ownerFullName:
    'Screening: screened as an individual against OFAC / UN / EU / OpenSanctions + PEP lists and GDELT adverse media. A sanctions match is a hard stop (score capped, Decline); PEP → PEP_MATCH reason code.\n' +
    'MATCH / TMF inquiry (principal).',
  ownerDateOfBirth:
    'Screening: birth year matching the listing adds +8% to the match score (raises severity to High); a different year subtracts 15% (helps clear false positives).',
  ownerNationality:
    'Screening: ±5% on the match score when the listing country agrees / disagrees. Passed to MATCH as principal country.',
  ownerRole:
    'Label only: shown on the screening subject and MATCH principal; not used in scoring.',
  ownershipPercent:
    'Recorded on the case / MATCH principal for UBO documentation; not used in scoring.',
  actor:
    'Audit only: recorded as the actor on the assessment.completed audit event and as the case creator.',
  externalRef:
    'Case only: stored as the case external reference (falls back to the assessment id). Not used in scoring.',
  createCase:
    'When on, a case is opened with the decision, reason codes and decision-log id, and an audit event is written. Off → case step is skipped (no effect on the decision).',

  // ---- Underwriting page -------------------------------------------------
  matchFound:
    'Credit model: direct input feature (30% component); a MATCH listing is a strong negative Shapley contributor (MATCH_LISTED reason code).\n' +
    'Reserve & pricing: forces rolling reserve ≥20% for ≥180 days and an upfront reserve.',
  explainClass:
    'Explainability only: which outcome class (Approved / Referred / Declined) the Shapley contributions are computed for. Blank = the predicted class. Does not change the decision.',
  kybHighRisk:
    'Reserve & pricing only: "Yes" adds +0.20 risk (KYB_HIGH_RISK) → worse band, higher rolling reserve and settlement delay. Unknown = no adjustment.',
  websiteComplianceScore:
    'Reserve & pricing: scores <60 add (60 − score) / 400 risk, up to +0.15 (WEBSITE_NON_COMPLIANT).\n' +
    'Unified score: the same value is the WebsiteCompliance component (10%); <60 raises the WEBSITE_NON_COMPLIANT reason code. Blank = not run → coverage gap.',
  volumePlausibilityScore:
    'Reserve & pricing: scores <60 add (60 − score) / 400 risk, up to +0.15 (VOLUME_IMPLAUSIBLE).\n' +
    'Unified score: the same value is the VolumePlausibility component (10%); <50 raises VOLUME_IMPLAUSIBLE. Blank = not run → coverage gap.',
  monthlyCardVolumeFromStatements:
    'Volume plausibility only: annualised (×12) and compared with the declared annual volume. Declared >1.5× statements −12 (DECLARED_ABOVE_STATEMENTS), >2.5× −30 (DECLARED_FAR_ABOVE_STATEMENTS); declared <0.5× statements −10 (DECLARED_BELOW_STATEMENTS, possible volume splitting). Blank → skipped.',
  bankCsv:
    'Bank statement analysis: rows are parsed into monthly inflows / outflows / net, card-processor deposits (by descriptor), NSF & overdraft fees, negative-balance days, volatility and seasonality → flags.\n' +
    'Implied annual card volume from the deposits feeds the plausibility comparison.',
  pnlText:
    'P&L / balance-sheet analysis: lines are matched to revenue, COGS, opex, net income, assets, liabilities, equity, cash → margins, leverage and liquidity ratios vs benchmark → flags.',
  declaredVolume:
    'P&L analysis only: compared with statement revenue (card volume far above total revenue → VOLUME_EXCEEDS_REVENUE).',

  // ---- KYB page -------------------------------------------------------------
  declaredMcc:
    'Prohibited & restricted business check: used as a category hint alongside the description and website text.\n' +
    'Website compliance scan: sets which policy pages are expected for the industry.',

  // ---- Unified score page -----------------------------------------------------
  merchantName:
    'Label only: recorded on the score signals, the decision log and the case title. Not used in scoring.',
  includeApplication:
    'When on, the LightGBM credit model runs on MCC / volume / tickets / MATCH / relationship and its probability of approval becomes the CreditModel component (30%). Off → component treated as not run (coverage gap).',
  kybRisk:
    'KYB component (20%): Low → 90/100, Medium → 55, High → 20 (+ KYB_HIGH_RISK reason code). Blank with "Business verified" also blank → component not run (coverage gap).',
  businessVerified:
    'KYB component (20%): "No" deducts 25 and raises BUSINESS_UNVERIFIED (High).',
  entityAgeMonths:
    'KYB component (20%): <12 months deducts 10 and raises NEW_ENTITY (Medium).',
  sanctionsMatch:
    'Screening component (15%): "Yes" → component 0 and hard stop SANCTIONS_MATCH (score capped, Decline via HARD_STOP_SANCTIONS rule).',
  pepMatch:
    'Screening component (15%): "Yes" deducts 40 and raises PEP_MATCH (Medium) → the PEP_EDD rule refers the case for enhanced due diligence.',
  adverseMedia:
    'Screening component (15%): "Yes" deducts 25 and raises ADVERSE_MEDIA (Medium).',
  prohibitedVerdict:
    'BusinessPolicy component (10%): Acceptable 100 · HighRisk 60 (HIGH_RISK_BUSINESS) · Restricted 35 (RESTRICTED_BUSINESS) · Prohibited 0 + hard stop PROHIBITED_BUSINESS → Decline.',
  termsRiskBand:
    'Pricing component (5%): A 95 · B 80 · C 60 · D 35 · E 15; D/E raise HEAVY_RESERVE_REQUIRED. Blank = not run → coverage gap.',
  matchListed:
    '"Yes" → hard stop MATCH_LISTED (High) → Decline via HARD_STOP_MATCH. "No" clears the check; blank = not checked (coverage gap).',

  // ---- MATCH page -------------------------------------------------------------
  matchLegalName:
    'MATCH / terminated-merchant inquiry: matched (with DBA) against the configured terminated-merchant list. A listing is a hard stop in the unified score.',
  doingBusinessAs:
    'MATCH inquiry: secondary business-name match alongside the legal name.',
  matchTaxId:
    'MATCH inquiry: exact identifier match — the strongest evidence of a prior termination.',
  matchAddress:
    'MATCH inquiry: address, city, region and postal code are matched together to catch the same merchant re-applying under a new name.',
  matchCountry:
    'MATCH inquiry: scopes the address / principal match to a jurisdiction (ISO-2).',
  principalName:
    'MATCH inquiry: principal first + last name matched against terminated-merchant principals (catches owners re-applying with a new entity).',
  principalDateOfBirth:
    'MATCH inquiry: disambiguates principals with common names; a matching birth date raises the match confidence.',

  // ---- MCC validator page -------------------------------------------------------
  mcc:
    'MCC validation: the declared code is compared with what the website evidence suggests (text classifier, structured data, EDGAR SIC mapping). Mismatch → Inconsistent / Refer. Also sets the risk tier shown alongside the code.',
  mccWebsite:
    'MCC validation: pages are crawled and classified to infer the real business category; the SEC filer index is checked for a matching company. The evidence score drives the verdict.',

  // ---- Cases / webhooks -----------------------------------------------------------
  casePriority:
    'Case queue ordering only; does not affect any score or decision.',
  actorRequired:
    'Audit only: written as the actor on every case / audit event so the hash-chained log records who did what.',
  webhookUrl:
    'Endpoint that receives signed JSON events (assessment.completed, case.updated, …). Deliveries and failures are listed below.',
  webhookSecret:
    'Used to HMAC-SHA256 sign every delivery (X-MI-Signature header) so the receiver can verify authenticity.',
  webhookEvents:
    'Which platform events are pushed to this endpoint.',

  // ---- Rules / models -----------------------------------------------------------------
  ruleAuthor:
    'Governance only: recorded on the published rule-set version and in the audit chain. Required to publish.',
  ruleComment:
    'Governance only: change note stored with the rule-set version for reviewers.',
  ruleEditor:
    'The policy rules applied after scoring: each rule has a priority, an outcome (Approve / Refer / Decline) and a condition over the score facts (score, tier, hardStops, reasonCodes, matchFound …). Lowest priority wins; hard stops are priority 1. Publishing activates the new version immediately.',
  ruleFacts:
    'Dry-run input: the flattened scoring result the rules are evaluated against (score, tier, sanctionsMatch, annualVolume …). Nothing is stored.',
  syntheticRows:
    'Retraining only: number of synthetic applications generated to augment the labelled decisions before training the challenger model.',
  registerAsChallenger:
    'When on, the retrained model is registered as challenger so it can be compared against the champion (PSI drift, agreement) before promotion. Off → trained and evaluated only.',
  justification:
    'Governance only: reason recorded in the model registry and audit chain when the challenger replaces the champion.'
};
