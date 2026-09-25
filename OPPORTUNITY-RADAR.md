# Opportunity Radar operations

Opportunity Radar is an admin-only workflow covering two entity types: **ActiveProject** and **BusinessProspect**. Jev is the only user-facing evaluator. Application code deterministically converts stored Jev judgments into score, confidence, priority, recommendation, and explainable checks. Imported descriptions must be public or otherwise non-confidential. Source URLs are stored as metadata and are never fetched by the server.

## Local setup

Apply the pending local migrations only after Jonathan approves the checkpoint review:

```powershell
dotnet ef database update
```

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

Budget, scope, exclusions, and weight changes append immutable `LocalRecompose` evaluations from the latest valid Jev judgments. Capability changes can alter the Jev request, so Active Project evaluations become stale and require reevaluation. Saving only digest counts does not add evaluation history.

Active Project, Operational Pain, and Digital Presence each have independent relative weights. Hybrid prospects use the Operational Pain profile. Values are normalized to 100 before scoring. An all-zero group falls back to versioned defaults. Every evaluation stores its effective normalized profile.

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

The application does not crawl websites, send outreach, or claim Jev probabilities are win probabilities. It stores Jev probabilities, per-answer confidence, Noul probabilities, selected passages, resolved model, and usage. Imported research confidence, reason, agent identity, and prospect type remain separate and are never sent to Jev. There is no live external ingestion API; agent-produced findings arrive as CSV or paste imports.

Re-importing a record matches an existing one in this priority order: an exact `ExternalId` match (same entity type and synthetic flag) first, when the import supplies one; otherwise ActiveProject falls back to an exact content fingerprint (title, description, and source URL, so any rewording creates a new record instead), and BusinessProspect falls back to an exact website-domain match when a domain is present, or an exact business-name match when it is not. A match updates the existing row's source material in place (including its title and, for ActiveProject, its fingerprint) and never overwrites a decision or notes a human already recorded, but it does mark any prior `Ready` evaluation `Stale`, since it was computed from text that no longer exists.

The digest only ever surfaces records you have not yet reviewed: once a record has any decision recorded (Pursue/Investigate/Pass or Prioritize/Watch/Skip), it stops appearing in the digest even if the model's recommendation would otherwise still qualify.

## Checks and export

Each detail view shows classification agreement, deterministic Info, Review, and Block checks, and the effective scoring profile. Review checks require verification, prevent a top recommendation, and exclude the record from the digest. Block checks force Pass or Skip. Imported research confidence never changes Opportunity Score or Jev Confidence.

CSV export excludes synthetic examples by default. It includes source metadata, imported and evaluated types, confidence values, checks, effective weights, rubric version, evaluation origin, factors, review decision, notes, and model metadata. Every field is quoted, embedded quotes are doubled, control characters are replaced with spaces, and leading ASCII or full-width formula characters are prefixed as text. Sanitization affects only the exported copy.
