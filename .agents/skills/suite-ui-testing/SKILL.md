---
name: suite-ui-testing
description: Run the Angular merchant workbench against the local API and verify stateful browser workflows.
---

# Suite UI testing

Run `dotnet run --project src/MerchantIntelligence.Api` from the repository root (5292), and `npm start` from `web/mcc-validator` (4200). The Angular development proxy forwards `/api` to 5292. If dependencies are absent, install them using the package lock. Restart both services after machine suspension; SQLite state and model artifacts may persist.

## Devin Secrets Needed
None for local UI acceptance. Unconfigured MATCH should explicitly report Unknown/NotConfigured; do not substitute a clear result.

## Stateful workflow
Register a localhost webhook receiver before creating cases if delivery evidence is needed. Use a disposable secret of at least 16 characters, verify it is not echoed, inspect delivery status, then delete the registration.

Clean and sanctioned presets can create already-finalized cases by design. Use the sparse/credit-only preset for assignment, notes, status changes, and manual decisions. Do not assume the API exposes an explicit override field; inspect its current request contract before planning override checks.

Rules history revision numbers and the rules JSON's semantic version can differ. Rollback creates a new revision rather than deleting history.

Underwriting includes embedded bank CSV and financial text samples. Native file uploads submit immediately. For deterministic upload coverage, use controlled CSV fixtures and inspect result totals rather than only the selected filename.

Model retraining and promotion mutate persisted local state. Check numeric registry metrics separately from textual confusion tables and verify audit integrity after mutations. Drift may need at least 30 logged decisions; comparison may need 50 labelled outcomes.

Resize the browser below 900 CSS pixels to verify the menu, overlaid drawer, and close-on-navigation behavior. Restore maximized state afterward.
