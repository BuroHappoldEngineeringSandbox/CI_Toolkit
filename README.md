# CI_Toolkit (BuroHappoldEngineeringSandbox)

This is the **proxy repository** for CI enforcement in the BuroHappoldEngineeringSandbox org.

It contains no CI logic. Each workflow is a thin shim that forwards execution to the corresponding workflow in [BuroHappoldEngineeringAdmin/CI_Toolkit](https://github.com/BuroHappoldEngineeringAdmin/CI_Toolkit), which is the single source of truth for all CI behaviour.

## Role of this repo

Org-level rulesets in BuroHappoldEngineeringSandbox require workflows from this repo as required checks on pull requests. GitHub enforces that the named workflow passes before a PR can be merged.

```
PR in BuroHappoldEngineeringSandbox/<repo>
  → ruleset requires BuroHappoldEngineeringSandbox/CI_Toolkit/.github/workflows/ci-build.yml@develop
    → forwards to BuroHappoldEngineeringAdmin/CI_Toolkit/.github/workflows/ci-build.yml@develop
      → CI logic runs in Admin context
```

## Staging vs production

This proxy points to the `develop` branch of Admin/CI_Toolkit. BuroHappoldEngineeringSandbox acts as the **staging environment** — new CI features land on `develop` and are validated here before being promoted to `main` and rolled out to production orgs.

| Branch | Used by |
|---|---|
| `develop` | This proxy (staging — BuroHappoldEngineeringSandbox) |
| `main` | Production proxy deployments (BHoM, BuroHappoldEngineering) |

## Secrets required

The following org-level secrets must be configured in BuroHappoldEngineeringSandbox, scoped to this repository only:

| Secret | Purpose |
|---|---|
| `BHOM_APP_ID` | GitHub App ID — passed to Admin/CI_Toolkit via `secrets: inherit` |
| `BHOM_APP_PRIVATE_KEY` | GitHub App PEM key — used to mint tokens for cross-repo dependency resolution |

## Do not edit workflow files directly

Changes to CI behaviour must be made in [BuroHappoldEngineeringAdmin/CI_Toolkit](https://github.com/BuroHappoldEngineeringAdmin/CI_Toolkit). Edits to the workflow files in this repo will have no effect on CI logic — only the `uses:` target ref matters.
