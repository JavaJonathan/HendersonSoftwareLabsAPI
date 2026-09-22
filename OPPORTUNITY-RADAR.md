# Opportunity Radar operations

Opportunity Radar is an admin-only workflow covering two entity types: **ActiveProject** (explicit project/RFP demand) and **BusinessProspect** (an established business with a weak web presence, buying intent normally `Unknown`). Imported descriptions must be public or otherwise non-confidential. Source URLs are stored as metadata and are never fetched by the server. See `CLAUDE.md`'s "Opportunity Radar: split into ActiveProject / BusinessProspect" section for the full architecture; this file covers day-to-day operation.

## Local setup

Apply the pending local migrations only after Jonathan approves the checkpoint review:

```powershell
dotnet ef database update
```

The demonstration evaluator needs no additional configuration. It is deterministic and every result is labeled as simulated.

To enable live Jev locally, keep the key in .NET user secrets:

```powershell
dotnet user-secrets set "TypeSafe:ApiKey" "<key>"
```

Do not add the key to `appsettings.json`, source control, browser configuration, logs, or screenshots. Production should provide `TypeSafe__ApiKey` from SSM Parameter Store through the existing container environment-file flow.

## Live evaluation controls

The server calls only `https://api.typesafe.ai/v1/systemone`, with a 15 second timeout. The browser cannot provide an alternate destination. A live batch requires a fresh preview confirmation for the exact records and source versions.

Defaults in `appsettings.json`:

- Model alias: `jev-latest`. The resolved version returned by TypeSafe is stored with each result.
- Conservative input price: $0.042 per million tokens.
- Estimated maximum batch cost: $0.05.
- Rolling 24 hour input limit: 5,000,000 tokens.
- Maximum batch size: 100 records.
- Maximum concurrency: four requests.
- Maximum retries: two after the initial request, only for transport errors, 429, and 529. `Retry-After` is honored up to 10 seconds.

The cost preview uses the UTF-8 byte length of each exact serialized request as a conservative maximum token estimate. It is an estimate, not a provider quote. Actual token usage reported by Jev is persisted and counted against the rolling limit.

Provider failures create an explicit failed evaluation record. They do not erase imported data and never become a low score or Pass recommendation. Authentication and validation errors are not retried.

## Preference changes

Budget, scope, project preference, weights, and incomplete-information changes rescore stored judgments locally. Capability changes can alter the semantic fit question, so existing live Jev results become stale and require reevaluation. Simulated results are regenerated locally only when that entity type's preferences actually changed; saving preferences with only the digest counts changed does not add new evaluation history.

Weights are normalized to sum to 100 before scoring, regardless of the raw values entered. This keeps the High/Medium/Low priority thresholds meaningful no matter what an admin types; there is no requirement that the entered weights themselves sum to 100.

## Verification

Run the focused deterministic and mocked-provider checks:

```powershell
dotnet run --project tests/OpportunityRadar.Unit
```

The opt-in live contract sends one synthetic public description and incurs provider usage:

```powershell
$env:RADAR_RUN_LIVE_CONTRACT = "true"
$env:TYPESAFE_API_KEY = "<key>"
dotnet run --project tests/OpportunityRadar.Unit
```

The ordinary test run never calls TypeSafe. The live contract should only be run deliberately after reviewing the current price and server guardrails.

## Current limits

The application does not crawl websites, send outreach, or claim Jev probabilities are win probabilities. It stores Jev probabilities and confidence in the provider response metadata for later calibration. There is no live external ingestion API for either type; agent-produced findings arrive as CSV (or paste) imports, same as manual entry.

Re-importing a record matches an existing one in this priority order: an exact `ExternalId` match (same entity type and synthetic flag) first, when the import supplies one; otherwise ActiveProject falls back to an exact content fingerprint (title, description, and source URL, so any rewording creates a new record instead), and BusinessProspect falls back to an exact website-domain match when a domain is present, or an exact business-name match when it is not. A match updates the existing row's source material in place (including its title and, for ActiveProject, its fingerprint) and never overwrites a decision or notes a human already recorded, but it does mark any prior `Ready` evaluation `Stale`, since it was computed from text that no longer exists.

The digest only ever surfaces records you have not yet reviewed: once a record has any decision recorded (Pursue/Investigate/Pass or Prioritize/Watch/Skip), it stops appearing in the digest even if the model's recommendation would otherwise still qualify.

## Comparison and export

Each detail view compares the semantic evaluation with a literal keyword baseline. The baseline exposes matched terms, a coarse 0 to 3 keyword score, and every deterministic hard-rule outcome. Synthetic comparisons are labeled as illustrations, not benchmarks.

CSV export excludes synthetic examples by default. It includes source metadata, semantic factors, recommendation, baseline output, review decision, notes, and model metadata. Every field is quoted, embedded quotes are doubled, control characters are replaced with spaces, and leading ASCII or full-width formula characters are prefixed as text. Sanitization affects only the exported copy. Stored source and notes remain unchanged. CSV interpretation differs across spreadsheet programs, so exported files should still be treated as untrusted data when shared outside HSL.
