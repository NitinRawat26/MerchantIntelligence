# Step 7b · `owners` — Owner identity depth (principals, tenure, duplicates)

| | |
|---|---|
| Step id | `owners` |
| Agent | KYB (`kyb`) |
| Stage | 1, runs in parallel with `verification`, `screening`, `match` (no dependencies beyond the implicit Profile step) |
| Depends on | Profile (`segment`) implicitly; reads `ctx.Profile` for the SMB weighting |
| Can hard-stop | No |
| Consumed by | KybRisk (Kyb score component), `RiskSignal`s → reason codes, explainability check outcome *Owner identity*, analyst brief / next steps, workbench *Identity & screening* tab |
| Storage | `applicant_principals` table (normalised name + date-of-birth key, merchant, assessment id, seen-at) in the platform SQLite database |
| Implementation | `src/MerchantIntelligence.Platform/Owners/OwnerIdentity.cs` (assessor), `PrincipalRegistry.cs` (cross-application history), `src/MerchantIntelligence.Platform/Agents/Kyb/OwnerIdentityStep.cs` |

---

## 1. Why this step exists

### Functional purpose
Check the **declared principals themselves** — not whether they are sanctioned (that is `screening`) but whether the identity we have been given is complete, internally consistent with the business, and whether the same person has appeared behind other applications.

### Business question answered
*"Do we actually know who stands behind this merchant, does their story line up with the business's story, and have we seen them before?"*

### Merchant-acquiring rationale
For a **Micro or Small merchant the business is its owner**. A one-shop grill has no LEI, no filings, no financial statements; the durable, screenable, pursuable identity is the person who owns it. Bust-out and identity-fraud patterns in SMB acquiring are overwhelmingly owner-level: an incomplete identity (no date of birth, no address) that cannot be pursued after chargebacks, a "founder" who would have been fourteen when the business started, or the same individual submitting several unrelated shops in a few weeks. None of these is caught by registry verification or sanctions screening, which is why this is a separate step.

### What it proves — and what it does not
It establishes **completeness and consistency of the declared identity** and **application history within this platform**. It does **not** verify the identity against a government source (no ID document check, no credit-bureau hit) and it does not prove control — a person can be declared without being the true beneficial owner. The registered agent returned by a state register (e.g. Kentucky SOS) is **never** promoted to a principal; agents are a service address, not an owner.

---

## 2. Inputs

| Field | Source | Used for |
|---|---|---|
| `owners[].fullName` | intake | key for the cross-application registry |
| `owners[].dateOfBirth` | intake | age, age-vs-tenure, disambiguation in the registry key |
| `owners[].nationality` | intake | completeness |
| `owners[].role` | intake | control declared? |
| `owners[].ownershipPercent` | intake | under-declared ownership for SMB |
| `owners[].address` | intake (new) | home-based detection vs. business address |
| `yearsInBusiness` | intake | age-vs-tenure |
| `business.addressLine/city/postalCode` | intake | home-based detection |
| `profile.isSmb` | Profile agent | severity weighting |
| `applicant_principals` | platform database | duplicate / velocity |

---

## 3. Checks and findings

| Code | Severity | Condition |
|---|---|---|
| `OWNER_IDENTITY_INCOMPLETE` | Medium (SMB) / Low | any principal missing date of birth, nationality or address; message names the person and the gaps |
| `OWNER_ROLE_UNDECLARED` | Low | no principal has a role |
| `OWNER_UNDERAGE` | High | age < 18 |
| `OWNER_AGE_VS_TENURE` | Medium | `yearsInBusiness` > age − 16 (would have founded the business as a child) |
| `OWNERSHIP_UNDER_DECLARED` | Low (SMB only) | all percentages given and they total < 75 % |
| `OWNER_HOME_BASED` | Low | a principal's address matches the business address (≥ 0.85 address similarity) — informational; combined with the address-type and MCC checks elsewhere |
| `OWNER_DUPLICATE_APPLICATION` | Medium | the same person (name + DOB, or name only when no DOB is on file) was a principal on ≥ 1 earlier application for a **different** merchant |
| `OWNER_APPLICATION_VELOCITY` | High | ≥ 3 such applications in the last 90 days |

`CompletenessPercent` = supplied ÷ possible over (DOB, nationality, address) across all principals.

If **no principal is declared** the step is skipped with `Covered = false`; the explainability outcome is *No principal declared* (Medium for SMB) and the Kyb component treats it as missing evidence, not as clean. The Profile agent's `SMB_NO_OWNER` finding fires alongside.

---

## 4. Cross-application memory

```mermaid
flowchart LR
    A[Assessment N: owners declared] --> K[PrincipalKey = NAME|DOB]
    K --> Q{applicant_principals}
    Q -- other merchants, other assessments --> D[OWNER_DUPLICATE_APPLICATION / VELOCITY]
    Q -- same merchant re-assessed --> X[ignored]
    A --> R[Remember N's principals]
```

Re-assessing the same merchant does not count as a duplicate (merchant name is normalised and excluded). A DOB-less record matches on name only, so a common name may surface false positives — the message lists the earlier merchants and dates so the analyst can dismiss them.

---

## 5. Where the evidence surfaces

* **Explainability** — check outcome *Owner identity* (`Complete` / `n finding(s)` / `No principal declared`), narrative line with every code, next steps (collect ID/proof of address; review earlier applications).
* **Score** — flags enter `KybRisk` (worst severity) and the reason codes as `owners:<CODE>`.
* **Workbench** — *Identity & screening* tab: per-principal table (role, age, DOB/nationality/address supplied, prior applications) and the flag list; owner address field in the intake form.
* **Audit** — step summary `n principal(s) · identity 67% complete · home-based · 2 finding(s): …`.
