# Handover: known-failure baseline for `ci-dataset-tests`

Status as of **2026-09-07**. Prototype complete and verified end to end; not merged, not wired
into any production workflow, blocked on one decision (see §3.2).

Everything below is either evidenced with a run ID / file:line, or explicitly marked
**INFERRED**, **ASSUMED** or **HEURISTIC**. Treat unmarked claims as measured.

---

## 1. The problem

### 1.1 Two checks disagree, and both are right

`BHoM/BHoM_Engine` pull requests receive two independent dataset-test verdicts:

| | `ci-dataset-tests` (GitHub Actions) | `unit-tests` (BHoMBot) |
|---|---|---|
| Implemented by | `BHoM/CI_Toolkit` `.github/actions/ci-dataset-tests` → `tools/ComplianceRunner/src/DatasetTestRunner` | `BuroHappoldEngineeringAdmin/BHoMBot` `BotModules/CI/Compute/Checks/UnitTests.cs` |
| Host runtime | **net10.0** (`DatasetTestRunner.csproj`, `<TargetFramework>net10.0</TargetFramework>`) | **.NET Framework 4.7.2** (`ExtraProcesses/UnitTestProcess/UnitTestProcess.csproj`, `<TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>`) |
| Comparison code | `BH.Engine.UnitTest.Compute.CheckTest` from `UnitTest_Engine.dll` | the same `UnitTest_Engine.dll` |
| Comparison config | `BH.Engine.Test.Create.DefaultTestComparisonConfig()` | the same static singleton |
| Ref under test | the PR **merge commit** (`refs/pull/N/merge`) | the PR **branch head** |
| Scope | `.ci/Datasets/**/*.json` only | `.ci/Datasets/**/*.json` **and** `.ci/unit-tests/<Repo>_Tests.sln` via NUnit |
| Verdict on failure | red | green |

`UnitTest_Engine.dll` is netstandard2.0. Both hosts load the *same binary* and the *same*
`ComparisonConfig` (`NumericTolerance = 1e-12`, `MaxPropertyDifferences = 1000`). The comparison
logic is not merely equivalent, it is identical. Tolerance, comparison mode and pass criteria do
not diverge.

What diverges is the **CLR and C runtime underneath**. The fixtures in `.ci/Datasets` store
expected values that were produced under .NET Framework (they are generated from Rhino/Grasshopper,
which is a Framework host). Framework and CoreCLR do not agree bit-for-bit on
`double.ToString()`, `Math.Sqrt`, `Math.Atan`, `Math.Sin` or `Math.Cos`. Identical code over
identical inputs therefore produces outputs that differ in the last digits — and the harness
compares with an *absolute* `1e-12` tolerance against coordinates up to 7.3e6, where one ULP of a
double is already ~9.3e-10. The tolerance is finer than a single bit at that magnitude, so the
comparison demands bit-exact reproduction across runtimes.

BHoMBot reproduces the Framework-side values because it *is* Framework. Our runner cannot.

**Evidence that BHoMBot genuinely executes the comparison** (i.e. its green is not a no-op):
check-run `98963033056` on `BHoM/BHoM_Engine` PR #3604 concluded `failure` with annotations
naming `C:\ProgramData\BHoMGitHub\BHoM_Engine\.ci\Datasets\BHoM_Engine\Query\RoundToCeilingAwayFromZero.json`
and per-`TestData` inner results, plus a `/.ci/unit-tests/BHoM_Engine_Tests.sln` NUnit annotation.
That PR changed 7 `.cs` files, so `UnitTests.cs:93-94` ran **every** fixture, and all eight of the
fixtures that fail for us existed at that SHA and did not fail for it.

Separately, note that BHoMBot's `unit-tests` posts **no output on success** and discards
warning-level annotations, so its green carries no evidence either way on any given run. Use a
*failing* run to prove execution.

### 1.2 The failures are not caused by any pull request

Run `34100977352` job `101675175420` on `BHoM/BHoM_Engine`, branch
`Structure_oM-#3607-AddConnectionAllowanceFragment`, head `e690b7e6`, changed files only
`Structure_Engine/Query/GeneralMaterialTakeoff.cs` and `Structure_Engine/Query/MaterialComposition.cs`,
failed with a **strict superset** of the failures seen on the unrelated
`Geometry_Engine-#3609-add-fillet-function-for-polylines` branch (run `34061281204`, job
`101562155320`, head `a0538720`). Same nine fixtures on both, plus five attributable to that PR's
own changes.

The set is stable and current: run `34168944904` (2026-09-07 23:07 UTC, head `c6c92bf0`, 219
fixtures) still reports exactly the same nine.

### 1.3 Why a baseline

`DatasetTestRunner` has one verdict: `return mergedResult.Status == TestStatus.Error ? 1 : 0`.
That makes "nine accepted environment failures" and "one real regression plus eight accepted ones"
the same colour. The check is permanently red, nobody reads it, and the failure count drifted from
7 to 9 without anyone noticing. A baseline turns the accepted set into reviewable data so that
anything outside it is loud.

`ci-dataset-tests` is **not** a required status check on `BHoM_Engine` `develop` (the required
contexts carry no `ci-` names), so its red currently blocks nothing. Do not change that as part of
this work.

---

## 2. The failure categories

Nine failing unit tests across eight fixture files. `SectionPropertyAtParameter.json` contributes
two — this is why a fixture path alone is not a sufficient key.

| # | Fixture (under `.ci/Datasets/`) | Method | Category |
|---|---|---|---|
| 1 | `Geometry_Engine/Query/ClosestPoint.json` | `ClosestPoint(Ellipse,Point,Double)` | d **(assumed)** |
| 2 | `Geometry_Engine/Query/Distance.json` | `Distance(Point,Ellipse)` | d **(assumed)** |
| 3 | `Geometry_Engine/Query/LineIntersections.json` | `LineIntersections(Ellipse,Line,Boolean,Double)` | d **(assumed)** |
| 4 | `Geometry_Engine/Query/PlaneIntersections.json` | `PlaneIntersections(Ellipse,Plane,Double)` | **d (established)** |
| 5 | `Structure_Engine/Modify/Flip.json` | `Flip(Bar)` | a–c |
| 6 | `Structure_Engine/Query/Mass.json` | `Mass(Panel)` | a–c |
| 7 | `Structure_Engine/Query/SectionPropertyAtParameter.json` | `SectionPropertyAtParameter(Bar,Double)` | a–c |
| 8 | `Structure_Engine/Query/SectionPropertyAtParameter.json` | `SectionPropertyAtParameter(ISectionProperty,Double)` | a–c |
| 9 | `Verification_Engine/Query/ReportMessage.json` | `ReportMessage(ICondition,IConditionResult,IConditionReportingConfig)` | a–c |

### (a) String formatting

.NET Core 3.0 changed the default `double.ToString()` from 15 significant digits to
shortest-round-trippable. A test whose output is a *string containing a number* therefore prints
`6.000262860000012` where the fixture stores `6.00026286000001`, and the exact string comparison
fails. Same double, different text. `ReportMessage` is the clearest instance.

**Retires when:** Isak Naslund's Test_Toolkit comparer lands.

### (b) ULP-scale numeric

`NumericTolerance = 1e-12`, **absolute**, against coordinates up to 7.3e6 where 1 ULP ≈ 9.3e-10.
51 of the 72 numeric differences observed in earlier analysis were 1–3 ULP. No code change on the
BHoM side can fix these; they are ordinary JIT/runtime variation.

**Retires when:** the comparer gains a configurable relative tolerance (significant digits).
`BaseComparisonConfig` already carries `SignificantFigures` and `PropertySignificantFigures`;
Test_Toolkit never sets them.

### (c) Near-zero expectations

Where the expected value is itself tiny (e.g. `8.19e-12` vs `1.02e-11`), a *relative* tolerance
does not help either — the values differ by more than any sane relative bound while being
physically identical. These need a per-property absolute floor.

**Retires when:** the comparer handles both, which is what Isak is building.

### (d) Near-tangency branch — **NOT a tolerance problem**

`BHoM_Engine/Geometry_Engine/Query/PlaneIntersection.cs`, the
`PlaneIntersections(this Ellipse curve, Plane plane, double tolerance)` overload, decides how many
points to return with a hard branch:

```csharp
double sqrtTerm = a * a + b * b - c * c;
isOverlapping   = sqrtTerm > 0;
checkTangency   = Math.Abs(sqrtTerm) / (2 * Math.Max(curve.Radius1, curve.Radius2)) < tolerance;
...
if (checkTangency)
{
    Point tangentPt = ellipsePt((ts[0] + ts[1]) / 2);
    if (tangentPt.Distance(plane) < tolerance)
        return new List<Point> { tangentPt };   // ONE point
    ...
}
return ts.Select(ellipsePt).ToList();           // TWO points
```

The fixture contains a tangency sweep over an ellipse with `Radius1 = 1000000.0`,
`Radius2 = 1.0`, the plane normal nearly equal to `Axis2`, and the plane origin stepping by ~6e-8
per row. Reimplemented in double precision over all **501** `TestData` rows in
`PlaneIntersections.json`:

- Row **453** (the one that expects `-1022.7676520933219` and produces `1.50…`):
  `tangentPt.Distance(plane)` = `9.999999999641203e-07` against `tolerance` = `1e-6`.
  A margin of **3.6e-11 relative**.
- **47 of 501 rows** sit within `1e-6` relative of that threshold.
- The single point our run returns, `(1.5028400468425864, 0.5661535072741959, 1.21853904798359)`,
  is bit-nearly the value row **454** stores as *its* expected single point. The run crosses the
  tangency threshold one row early.

So the enormous output delta (−1022.77 → 1.50) is produced by a **sub-ULP** input difference
amplified through a discontinuous branch. No comparer setting can absorb a change in the *shape*
of the result (two points vs one). This needs an engine change: the near-tangency decision must be
scale-relative rather than an absolute `< tolerance` on an ill-conditioned quantity.

**Retires when:** somebody changes `PlaneIntersection.cs`. Nobody has started. No issue is open
that I could find.

> **UNVERIFIED / HEURISTIC.** Fixtures 1–3 (`ClosestPoint(Ellipse,…)`, `Distance(Point,Ellipse)`,
> `LineIntersections(Ellipse,…)`) are *assumed* to share category (d) because they are Ellipse
> overloads and Ellipse geometry in this engine is built on the same near-degenerate parametric
> solve. **This has not been established.** The prototype's category rule is literally
> `if ($e.methodKey -match 'Ellipse')`. Somebody must read those three implementations and
> categorise them properly before a real baseline is committed. If any of them turn out to be
> (a–c), they should be `category: comparer, match: exact` instead. See §5.1.

---

## 3. What is decided

### 3.1 Design D: static file, method-keyed, fingerprinted, per-entry match mode

Five options were considered.

| Option | Per-entry reason | New failures loud | Stale visible | Drift caught | Maintenance | Outcome |
|---|---|---|---|---|---|---|
| A. Ignore whole fixture files | no | no | no | no | none | **Rejected** — would hide `SectionPropertyAtParameter`'s second overload entirely, since both live in one file |
| B. Dynamic base-branch leg | **no** | yes | n/a | partial | **zero** | **Rejected** — see below |
| C. Static file, method-keyed | yes | yes | yes | no | regenerate | Rejected — silently absorbs a changed failure set |
| **D. C + fingerprint + match mode** | yes | yes | yes | yes | regenerate | **Chosen** |
| E. Count threshold ("fail if > 9") | no | no | no | no | none | **Rejected** — the 7→9 drift would have passed |

**Why B was rejected, since it looks attractive.** `ci-serialisation` already does exactly this:
it rebuilds the base branch in the same job and diffs the two failure sets
(`.github/actions/ci-serialisation/action.yml` lines ~190-270,
`tools/SerialiserRunner/src/SerialiserRunner/Commands/CompareCommand.cs`). It needs no
maintenance, it self-updates as fixes land, and the wiring is proven in production. Three reasons
it is wrong here:

1. **It cannot see the drift you care about.** The 7→9 growth happened on `develop`. A base-branch
   leg would have failed identically on both legs and cancelled — reporting "no regression" the
   entire time. That is the exact failure mode this work exists to fix.
2. **It cannot carry a reason.** There is no list, so there is nowhere to record "waiting on
   Isak's comparer" versus "blocked on an engine change nobody has started". Those need different
   escalation.
3. **It cannot shrink deliberately.** When entries stop failing it just quietly passes; nobody is
   prompted to remove anything or to notice that a fix landed.

A checked-in file makes both drift and shrinkage a reviewable diff in a pull request. Option B's
`implausible_baseline_ratio` guard (refuse to diff against a baseline where most of the population
fails) is still worth stealing if a second leg is ever added.

### 3.2 File location: caller repo — **DECISION PENDING**

The baseline belongs at `.ci/dataset-test-baseline.json` **in the caller repo**, next to the
fixtures it describes, so that repo owners review changes to it in the same pull request as the
code change that caused them.

**This is blocked.** Creating that file is a change to `BHoM_Engine`, and the constraint on this
work is that `CI_Toolkit` is the only writable repo. Nothing has been created. The action already
defaults `baseline_file` to that path and degrades to today's behaviour when the file is absent,
so the CI_Toolkit side is ready.

**Why not central (one file per repo inside CI_Toolkit):** it couples CI_Toolkit to caller data,
puts review of a geometry team's accepted failures in the DevOps team's queue, and scales badly
across the ~55 repos still to onboard. It is possible if the decision goes the other way; the
`baseline_file` input takes any path.

### 3.3 Keying

```
<repo-relative fixture path>::<method signature reduced to parameter types>
```

Example: `.ci/Datasets/Geometry_Engine/Query/PlaneIntersections.json::PlaneIntersections(Ellipse,Plane,Double)`

The method key is derived from the unit test's `Description`, which `CheckTest` builds as
`"UnitTest: Method: {method.ToText()}"` plus `" ,name: {name}."` when the `UnitTest` carries a
name. `ToText()` includes parameter **names**, e.g.
`PlaneIntersections(Ellipse curve, Plane plane, Double tolerance)`. `DatasetBaseline.NormaliseMethodKey`
strips the prefix, splits parameters at **top-level** commas only (so a generic argument list
survives), drops the trailing parameter name from each, and appends `#name` when a test name is
set. Unparseable input falls back to the trimmed description.

**Survives:** row reordering, row additions, re-serialisation of the fixture, parameter renames.
The key contains no ordinal and no parameter name.

**Invalidates the key (all legitimately):** a genuine method signature change, renaming or moving
the fixture file, adding or changing `UnitTest.Name`.

**Cannot key below the method.** A `TestData` row's only identifier in the result tree is its
ordinal (`"TestData: index: 37"`), which moves. The fixture JSON does carry a stable
`BHoM_Guid` per `TestData`, but `CheckTest` never surfaces it. See §5.4.

### 3.4 Fingerprint

Per baselined method, an order-independent 16-hex-char SHA-256 digest over the sorted multiset of
`"{ComparisonDifference.Property}|{canonical(ComparisonDifference.ReferenceValue)}"` lines.

- **Deliberately excludes `RunValue`.** The value the run produces varies by a few ULP between
  runtimes and machines; including it would make the fingerprint churn on exactly the failures the
  baseline exists to hold.
- **Includes `ReferenceValue`,** which is read from the stored fixture and never varies. Doubles
  are rendered with `"R"` under `InvariantCulture` so a 17-significant-digit stored value survives
  verbatim.
- **Sorted** so reordering cannot change it; **multiplicity preserved** so a second row failing on
  the same property does change it.
- A failing row that produces *no* `ComparisonDifference` (the method threw, or returned a
  different number of outputs — `CheckTest` returns early with only a `Message` and an empty
  `Information`) contributes `"!" + first line of the message`. The exception path's text is not
  stable, so such entries should use `match: method`.

Purpose: catch a *changed* failure under an unchanged key. An entry that fails differently is not
the entry that was accepted.

### 3.5 Match modes — both are needed

- `match: exact` — the fingerprint must still hold. Used for categories (a–c), whose failure shape
  is stable: the same rows fail on the same properties every run, only the last digits move, and
  those are excluded from the fingerprint.
- `match: method` — any failure under the key is accepted, whatever its shape. Used for category
  (d), because the near-tangency branch legitimately flips between returning one and two points,
  which changes the difference set without the defect changing. An exact fingerprint would report
  drift on an unchanged bug and cry wolf.

An entry with an empty `fingerprint` (hand-added, or written by an older generator) degrades to
`match: method` rather than reporting drift against `""`.

### 3.6 `stale_policy` defaults to `warn`, not `fail`

A stale entry is one that is in the baseline, whose fixture ran, and which did not fail. It is
reported as a warning and in the job summary, and the job passes.

Failing on stale would turn every repo red the moment Isak's comparer lands and before anyone has
regenerated a baseline — punishing the fix. Move to `fail` once the set is stable. The input
exists (`stale_policy: warn|fail`), and the runner flag is `--stale-fails`.

**Staleness is scoped to fixtures that actually ran.** On a dataset-only change the action hands
the runner a subset; without that scoping every other baselined entry would report stale.

---

## 4. What is built

### 4.1 Location

| | |
|---|---|
| Repo | `BuroHappoldEngineeringSandbox/CI_Toolkit` (**not** `BHoM/CI_Toolkit`) |
| Branch | `proto/dataset-test-baseline`, tip `08e53923f5089075b3c3e99d6496ac3b3d2bf9bc` |
| Base | `BHoM/CI_Toolkit@afacb708b0a8b9990df8f6f28017bb49a8add924` (`develop` tip, unchanged as of 2026-09-07) |
| Diff | 7 files, +1459 / −8 |
| PR | none opened, deliberately |

Commits, oldest first:

```
e0a9ace prototype(dataset-tests): known-failure baseline for ci-dataset-tests
230685b prototype(dataset-tests): wire the baseline into the ci-dataset-tests action
4039ff7 fix(dataset-tests): read Description off TestResult, not ITestInformation
2eb972e fix(dataset-tests): classify before emitting, so a known failure is not an error
08e5392 fix(dataset-tests): do not serialise the computed Key into the baseline file
```

### 4.2 Files

| Path | What |
|---|---|
| `tools/ComplianceRunner/src/Shared/Compliance.Shared/DatasetBaseline.cs` | **New, 524 lines.** BHoM-free. Record types, keying, fingerprinting, `Classify`, `Generate`, JSON round-trip, job-summary markdown. Decides nothing about exit codes. |
| `tools/ComplianceRunner/src/Shared/Compliance.Shared/ArgParser.cs` | `DatasetArgs` gains `BaselinePath`, `WriteBaselinePath`, `DumpFailuresPath`, `StaleFails`, `GeneratedStamp`, all defaulted so existing callers are unaffected |
| `tools/ComplianceRunner/src/DatasetTestRunner/DatasetTestRunner.cs` | `CollectFailures` walks the result tree into records; classification runs before `OutputEmitter.Write`; new exit-code policy |
| `tools/ComplianceRunner/tests/Compliance.Unit.Tests/Unit/DatasetBaselineTests.cs` | **New, 25 tests** across keying, fingerprinting, classification and generation |
| `tools/ComplianceRunner/tests/Compliance.Unit.Tests/Compliance.Unit.Tests.csproj` | links `DatasetBaseline.cs` (follows the existing convention: BHoM-free shared files are compiled in, not project-referenced) |
| `.github/actions/ci-dataset-tests/action.yml` | new inputs `baseline_file`, `stale_policy`, `regenerate_baseline`; artifact upload on regeneration |
| `.github/workflows/probe-dataset-baseline.yml` | **New, 291 lines.** `workflow_dispatch` harness, see §4.5 |

`DatasetBaseline.cs` is deliberately free of BHoM types (matching `FileAccounting.cs` and
`SarifBuilder.cs`) so it compiles into the hermetic test project and runs on a bare runner. All
BHoM interaction — the result-tree walk — lives in `DatasetTestRunner.cs`.

### 4.3 Runner CLI

```
DatasetTestRunner [--output console|github|json|sarif] [--sarif-file PATH]
                  [--baseline PATH] [--write-baseline PATH]
                  [--dump-failures PATH] [--stale-fails]
                  [--generated-stamp TEXT]
                  <file1.json> [file2.json ...]
```

Verdict:

- No `--baseline` and no `--write-baseline` → **exactly** today's behaviour, `Error` ⇒ exit 1.
- `--write-baseline` → writes and exits 0. Regeneration reports, it does not gate.
- `--baseline` → exit 1 if any failure is **New** or **Drifted**; exit 1 on **Stale** only with
  `--stale-fails`; otherwise exit 0.
- `--baseline` pointing at a missing or malformed file → exit 1 with an explicit error. A baseline
  that cannot be read must not degrade to "no baseline", which would report all nine known
  failures as new and bury the real problem under them.
- Accepted failures are emitted as `::notice`, not `::error`, and the status handed to
  `OutputEmitter` drops to `Warning`. See §6.5 for why this matters.

### 4.4 Action inputs

| Input | Default | Effect |
|---|---|---|
| `baseline_file` | `.ci/dataset-test-baseline.json` | Passed as `--baseline` **only if the file exists**. Absent ⇒ the runner is invoked with the arguments it used before this existed, and a `::notice` says so out loud. |
| `stale_policy` | `warn` | `fail` adds `--stale-fails` |
| `regenerate_baseline` | `false` | `true` swaps to `--write-baseline` and uploads `dataset-test-baseline.generated.json` as an artifact. **Nothing is committed** — the file belongs to the caller repo, so a human takes the artifact, fills in `category` and `reason` for any new entry, and opens a PR. |

The action is **not** wired into any tier bundle. `templates/BHoM/ci-beta.yml` is untouched.

### 4.5 The probe harness

`.github/workflows/probe-dataset-baseline.yml`, `workflow_dispatch`, inputs `caller_repo`
(default `BHoM/BHoM_Engine`) and `caller_ref` (default `develop`).

It checks the caller out at the workspace root (matching production, so `PathHelper` relativises
fixture paths to `.ci/Datasets/…`), checks this branch out into `_cit`, calls the **unmodified**
`BHoM/CI_Toolkit@develop` `mint-dep-token` and `resolve-dependencies`, builds the caller solution,
publishes the runner from `_cit`, then runs four scenarios.

It deliberately does **not** call `ci-dataset-tests` (its nested action refs are pinned at
`BHoM/CI_Toolkit@develop`, so calling it would exercise develop's runner, not this branch's) and
does **not** call `prepare-runner` (its `runner_source_repo` mode has caused attribution problems
in sandbox validation before). `resolve-dependencies` *is* the production action on purpose: the
environment the fixtures execute in has to be the production one or the failure set means nothing.

`runs-on: windows-latest`, not `windows-2025-vs2026` — the sandbox org has no self-hosted runners
(`orgs/BuroHappoldEngineeringSandbox/actions/runners` returns `[]`) and the custom label would
queue forever.

### 4.6 How to reproduce

```bash
gh workflow run probe-dataset-baseline.yml \
  --repo BuroHappoldEngineeringSandbox/CI_Toolkit \
  --ref proto/dataset-test-baseline
```

Takes ~9 minutes. Artefacts (`dump.json`, `baseline-seed.json`, `baseline-full.json`,
`baseline-missing-one.json`, `baseline-with-stale.json`, `baseline-regenerated.json`) upload under
artifact name `dataset-baseline-probe`.

Hermetic tests, no BHoM install needed, ~100 ms:

```bash
dotnet test tools/ComplianceRunner/tests/Compliance.Unit.Tests --nologo
# Passed! - Failed: 0, Passed: 96 (25 of them added by this work)
```

**Note:** `.github/workflows/probe-dataset-baseline.yml` also exists on sandbox `develop`. That is
required — `workflow_dispatch` only resolves a workflow by filename when the file is present on the
default branch, though the run itself uses the file from the dispatched ref. Remove both copies
together if you retire the probe.

### 4.7 Verified results

Run **`34168615581`**, conclusion `success`, against `BHoM/BHoM_Engine@acc84d2c835c23cdd24d515c9c2df55fb4dc1333`
(`develop`), 218 fixtures, 218 examined, none dropped.

```
Run A  capture           9 failure records, all keyed and fingerprinted
Sc. 1  full baseline     9 known, 0 new, 0 drifted, 0 stale  → exit 0
                         0 error annotations, 9 notice annotations
Sc. 2  one entry dropped 8 known, 1 new                      → exit 1
                         ::error names ClosestPoint(Ellipse,Point,Double)
Sc. 3  stale entry added 9 known, 1 stale                    → exit 0 (warn) / exit 1 (--stale-fails)
Regen  10 entries in     → 9 out; stale dropped; engine=4 carried; unknown=0
```

Earlier runs, for the record: `34166837503` (failed — `ITestInformation.Description` compile
error), `34167278260` (failed — `$LASTEXITCODE` propagation in the harness, not the runner),
`34167813367` (success, before the `[JsonIgnore]` fix).

The nine records as captured, with fingerprints:

```
ClosestPoint.json               ClosestPoint(Ellipse,Point,Double)                  rows=13 diffs=35 fp=5223a5eae9e0efee
Distance.json                   Distance(Point,Ellipse)                             rows=13 diffs=13 fp=edbcc3d25fa89b11
LineIntersections.json          LineIntersections(Ellipse,Line,Boolean,Double)      rows=3  diffs=6  fp=267ddc991fbcff67
PlaneIntersections.json         PlaneIntersections(Ellipse,Plane,Double)            rows=4  diffs=14 fp=0a7a88a0ab7b9492
Flip.json                       Flip(Bar)                                           rows=2  diffs=2  fp=ab13adf8ddde3a4b
Mass.json                       Mass(Panel)                                         rows=2  diffs=2  fp=75a3ef27a470b8ea
SectionPropertyAtParameter.json SectionPropertyAtParameter(Bar,Double)              rows=1  diffs=1  fp=85272e12ef3568ab
SectionPropertyAtParameter.json SectionPropertyAtParameter(ISectionProperty,Double) rows=1  diffs=1  fp=90d9704136cd7c88
ReportMessage.json              ReportMessage(ICondition,IConditionResult,IConditionReportingConfig) rows=24 diffs=24 fp=e2fcbbd894053789
```

A generated entry looks like this:

```json
{
  "fixture": ".ci/Datasets/Structure_Engine/Modify/Flip.json",
  "methodKey": "Flip(Bar)",
  "description": "UnitTest: Method: Flip(Bar bar).",
  "category": "comparer",
  "reason": "Expectations serialised under .NET Framework, executed on net10.0; absolute 1e-12 NumericTolerance cannot absorb the difference.",
  "issue": "BHoM/BHoM_Engine#3514",
  "match": "exact",
  "failingDataCount": 2,
  "differenceCount": 2,
  "fingerprint": "ab13adf8ddde3a4b"
}
```

`fixture`, `methodKey`, `description`, `failingDataCount`, `differenceCount` and `fingerprint` are
machine-derived and rewritten on every regeneration. `category`, `reason`, `issue` and `match` are
hand-authored and carried forward by key; a brand-new entry gets `category: "unknown"` and an empty
reason, which is the prompt to categorise it.

---

## 5. What is open

### 5.1 Categorise the three unestablished Ellipse methods — **blocking a real baseline**

`ClosestPoint(Ellipse,Point,Double)`, `Distance(Point,Ellipse)` and
`LineIntersections(Ellipse,Line,Boolean,Double)` are marked `category: engine, match: method` by a
heuristic (`methodKey -match 'Ellipse'`), not by analysis. Read their implementations in
`BHoM_Engine/Geometry_Engine/Query/` and determine whether each is the near-tangency branch
problem (d) or ordinary precision (a–c). Wrong categorisation is not merely cosmetic: `match:
method` is a looser gate, so mislabelling an (a–c) entry as (d) means a genuine change in that
fixture's failure would pass silently.

### 5.2 Fingerprint stability across runs — **unmeasured**

Every fingerprint above comes from runs of the *same* runner image on the *same* caller SHA. It is
**INFERRED** that fingerprints are stable run to run for `match: exact` entries, on the reasoning
that `ReferenceValue` is read from the fixture and the set of *which* rows fail is deterministic
for a given binary. That has not been demonstrated across a runner-image change or a dependency
rebuild. If it turns out to churn, `match: exact` becomes unusable and the design degrades to
option C. Cheapest test: dispatch the probe twice a week apart and diff `baseline-seed.json`.

### 5.3 No-baseline-file behaviour — decide before rollout

Today a repo with no baseline file gets a `::notice` and today's behaviour (any failure ⇒ red).
That is right for the ~55 repos with no `.ci/Datasets` at all (they skip anyway), but for a repo
that *has* fixtures and no baseline it means the check stays permanently red, which is the status
quo. Options: leave it, or fail loudly demanding a baseline, or default to a repo-level opt-in.
Not decided.

### 5.4 Ask for Isak: populate `TestResult.ID`

`BH.oM.Test.Results.TestResult` declares `public virtual string ID { get; set; }`, documented as
"Machine readable identifier for the Test Result". `CheckTest` never sets it. If the per-`TestData`
results carried the `TestData`'s `BHoM_Guid` there, the baseline could key at row granularity and
the fingerprint would become a precision tool rather than a change detector. Worth raising while
he is in that code. **The prototype does not depend on it** — do not block on this.

### 5.5 Assembly loading — deliberately deferred

`DatasetTestRunner` calls `BH.Engine.Base.Compute.LoadAllAssemblies()`. BHoMBot's
`UnitTestProcess` loads only files ending `oM.dll`, `_Engine.dll`, `_Adapter.dll`, `_Test.dll`
from `C:\ProgramData\BHoM\Assemblies`. Whether that difference contributes to the failure set is
an open question and was **explicitly excluded** from this work: changing it mid-investigation
would make the results unattributable. Do not change it while establishing a baseline.

### 5.6 The 7 → 9 drift

An earlier measurement (2026-08-18, sandbox control run over 209 fixtures) recorded **7** baseline
failures. It is now **9**; `SectionPropertyAtParameter(Bar,Double)` and
`SectionPropertyAtParameter(ISectionProperty,Double)` are the additions. Nobody noticed, because
the check is non-blocking and nobody reads it. When did they appear and why? Not investigated. A
committed baseline would have made this a PR diff. It is the single best argument for this work.

---

## 6. Constraints and gotchas

### 6.1 Scope rules in force

- Work in `BuroHappoldEngineeringSandbox` and forks only. **No pushes to `BHoM/*`, no PRs against
  it, no comments on its PRs, no re-running its checks, no dataset edits.**
- **`CI_Toolkit` is the only writable repo.** If work appears to need `Test_Toolkit`,
  `BHoM_Engine`, fixture files, or anything else — **stop and flag it**, do not work around it and
  do not fold it silently into a summary. §3.2 is the live instance of this.
- Do not make any `ci-` check a required status context.
- Do not change assembly loading (§5.5).

### 6.2 `Compliance.Shared` does not build locally

`dotnet build tools/ComplianceRunner/src/DatasetTestRunner` fails with `CS0234: The type or
namespace name 'CodeCompliance' does not exist in the namespace 'BH.oM.Test'`. Cause:
`AnnotationConvert.cs` needs `CodeComplianceTest_oM.dll`, which is built by `BHoM/Test_Toolkit`
(`CodeCompliance_oM/CodeComplianceTest_oM.csproj`) and is **not** in the BHoM installer payload,
so even a machine with BHoM installed cannot build it. This is pre-existing and documented in
`Compliance.Unit.Tests.csproj`.

**Consequence: every change to `DatasetTestRunner.cs` costs a ~9-minute CI round trip.** Two of
the four probe runs were burned on compile/harness errors that a local build would have caught in
seconds. Mitigation: put every line of logic that can be BHoM-free into `DatasetBaseline.cs`,
which *is* locally testable, and keep `DatasetTestRunner.cs` to the thinnest possible tree walk.

### 6.3 GHA propagates `$LASTEXITCODE` under `shell: pwsh`

A step whose last native command exits non-zero **fails**, even when the script's own logic has
already handled it. Any harness step that deliberately expects a non-zero runner exit must end
with an explicit `exit 0`. Local PowerShell 5.1 behaves the opposite way, so this only shows up on
a real runner. Cost one full probe run (`34167278260`).

### 6.4 `ITestInformation` vs `TestResult`

`BH.oM.Test.ITestInformation` declares **only** `Message`, `Status`, `UTCTime`. `Description`
(which carries the method signature) and `Information` (the child rows) are declared on the
concrete `BH.oM.Test.Results.TestResult`. The entire tree walk therefore depends on
`as TestResult` casts. Cost one full probe run (`34166837503`).

The tree `CheckTest` builds is three deep:

```
TestResult                 (one per fixture file)   Description = "Unit test set: {name}. Author: {author}"
└─ TestResult              (one per UnitTest)       Description = "UnitTest: Method: {method.ToText()}."
   └─ TestResult           (one per TestData row)   Description = "TestData: index: {n}"
      └─ ComparisonDifference                       Property, ReferenceValue, RunValue
```

Only the top level is turned into annotations by the pre-existing code, which is why the job log
carries one truncated message per method and nothing keyable.

### 6.5 Classification must run *before* `OutputEmitter.Write`

Measured on run `34167813367`'s predecessor: with the verdict applied after emission, a run where
the baseline accounted for all nine failures exited 0 **and still printed nine `::error` lines**.
A green check covered in errors is the same "nobody reads it" problem one step further along. The
runner now classifies first, downgrades accepted failures to `notice`, and drops the emitted
status to `Warning`. The probe asserts zero error annotations on a passing run.

### 6.6 `Annotation.RuleName` and `BHoMGuid` are empty for dataset tests

`AnnotationConvert.ToAnnotationEquivalent` populates `RuleName`, `BHoMGuid`, `DocumentationUrl`
and `Location` **only** when the finding is a `BH.oM.Test.CodeCompliance.Error`. Dataset findings
are `TestResult`s, so all of those are empty and the only identity an annotation carries is the
fixture path the runner injects. This prototype sets `a.RuleName` to the method key so accepted
failures can be downgraded without re-parsing message text; it is also the natural SARIF ruleId.

### 6.7 Sandbox push mechanics

- Cloning `BHoM/CI_Toolkit` shallow (`--depth 1`) and pushing that history to the sandbox remote
  fails with `remote unpack failed: index-pack failed`. `git fetch --unshallow` first.
- `gh workflow run <file.yml>` returns HTTP 404 unless the workflow exists on the **default**
  branch, regardless of `--ref`.

### 6.8 Other repo conventions worth knowing

- Action pins in this repo are SHA-pinned with a version comment. Reuse the existing pins:
  `actions/checkout@de0fac2e…` (v6.0.2), `actions/setup-dotnet@c2fa09f4…` (v5),
  `actions/upload-artifact@ea165f8d…` (v4.6.2), `actions/cache/*@27d5ce7f…` (v5.0.5).
- `Compliance.Unit.Tests` is **NUnit**; `SerialiserRunner.Tests` and `VersioningRunner.Tests` are
  **xUnit**. Match the project you are in.
- `.github/workflows/test-tools.yml` only triggers on `paths: tools/**`. Changes under
  `.github/actions/**` do not run the runner tests.

---

## 7. Facts that are wrong elsewhere

Both of these are stated in the comment thread on `BHoM/BHoM_Engine` PR #3610
(https://github.com/BHoM/BHoM_Engine/pull/3610). Do not rediscover them from there.

### 7.1 "our dataset tests execute on .NET 6" — **wrong, it is net10.0**

`tools/ComplianceRunner/src/DatasetTestRunner/DatasetTestRunner.csproj` declares
`<TargetFramework>net10.0</TargetFramework>`, and the `ci-dataset-tests` action publishes it with
`runner_dotnet_version` defaulting to `10.x`. The substantive point in that comment — CoreCLR
versus .NET Framework, and the post-.NET-Core-3.0 `double.ToString()` change — is correct and
unaffected. Only the version number is wrong.

### 7.2 "the 21 large deltas are probably list ordering" — **wrong, it is the tangency branch**

Stated in the thread as a standing hypothesis. It is disproved. The mechanism is the hard
`if (tangentPt.Distance(plane) < tolerance)` branch in
`BHoM_Engine/Geometry_Engine/Query/PlaneIntersection.cs` described in §2(d): the result changes
**shape** (two points to one), it is not a permutation of the same points. The expected pair and
the produced single point are not the same values in a different order — the single point is
approximately the value the *next* fixture row stores.

Corollary that also contradicts the thread: "these deltas are too large to be precision artefacts"
is wrong. They *are* precision artefacts — a 3.6e-11 relative margin — amplified through a
discontinuous branch. Magnitude of output difference says nothing about magnitude of input
difference when a branch sits in between.

### 7.3 A third, milder one

The thread implies BHoMBot's green `unit-tests` is evidence the fixtures pass. It is, but not for
the reason usually assumed: BHoMBot posts **no output at all on success** and discards every
warning-level annotation, so a green tells you nothing about what ran. The proof that it really
executes the comparison is a *failing* run (check-run `98963033056` on PR #3604), not a passing
one.
