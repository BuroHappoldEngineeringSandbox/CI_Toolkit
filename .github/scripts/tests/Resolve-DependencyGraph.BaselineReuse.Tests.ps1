# Resolve-DependencyGraph.BaselineReuse.Tests.ps1
#
# Demonstrates findings-register item 4: ci-serialisation's baseline leg is built against the
# BRANCH's dependency code, so a regression introduced on the dependency side appears in both
# legs, compares equal, and the check passes.
#
# These began as a demonstration of the defect and are now the regression suite for its fix.
# The assertions in "link 4" and "reporting" were the INVERTS-ON-ITEM4 ones; they have been
# inverted and now assert the corrected behaviour. The link 1, link 2 and link 3 cases were
# controls and are unchanged, which is what makes them useful: they show the fix did not alter
# resolution for the single-invocation callers, which is every check except ci-serialisation.
#
# ci-serialisation remains under a standing gate and is not promoted to required by this fix.
#
# Why a hermetic test rather than a sandbox repo pair. Reproducing item 4 end to end needs a
# subject repo whose dependencies.txt names a dependency in the same org, plus a matching
# branch name on both. Every sandbox repo's dependencies.txt points at production BHoM repos,
# so the end-to-end case cannot be built without creating branches in production. This test
# reaches the same mechanism with a local bare repo and no network.
#
# What it establishes, in order:
#   1. On a fresh clone root, the resolver honours PR_BRANCH when that branch exists on the
#      dependency (the intended cross-repo feature).
#   2. Invoked a second time against the SAME clone root, it does not re-check-out anything,
#      and records the same SHA. This is the "already cloned" path at
#      Resolve-DependencyGraph.ps1:91 and it is what ci-serialisation's baseline leg hits.
#   3. The resolver CAN land on the base branch when asked to, so the defect is not that it
#      cannot; it is that ci-serialisation never asks. PR_BRANCH is sourced from
#      github.event.pull_request.head.ref in resolve-dependencies/action.yml:132, which is
#      constant for the whole job and has no per-invocation override.
#   4. An existing clone is now re-resolved, so an explicit base-branch request is honoured,
#      the working tree moves and not just the recorded SHA, and the two legs get different
#      cache-key inputs. Before the fix this Context asserted the opposite and was the proof
#      that two independent causes had to be addressed together.
#
# Run locally:  pwsh -Command "Invoke-Pester .github/scripts/tests -Output Detailed"
# Run in CI:    lint-workflows.yml, the powershell-tests job.

BeforeAll {
    $script:repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
    $script:resolver = Join-Path $repoRoot '.github/actions/resolve-dependencies/scripts/Resolve-DependencyGraph.ps1'
    $script:sandbox  = Join-Path ([IO.Path]::GetTempPath()) ("item4-" + [Guid]::NewGuid().ToString('N'))

    # The resolver hard-codes https://github.com/<owner>/<repo>.git, so the only way to point
    # it at a local fixture is git's insteadOf rewrite. Same mechanism the resolver itself
    # uses for tokens. Removed in AfterAll; nothing else in the powershell-tests job clones
    # from github.com after this file runs.
    $script:remoteRoot = Join-Path $sandbox 'remotes'
    New-Item -ItemType Directory -Force -Path $remoteRoot | Out-Null
    $script:insteadOfKey = 'url.file:///' + ($remoteRoot -replace '\\', '/') + '/.insteadOf'
    git config --global $insteadOfKey 'https://github.com/'

    # The resolver appends a markdown table to the step summary when it clones. Redirect it so
    # a test run does not write into the real job summary.
    $script:savedSummary = $env:GITHUB_STEP_SUMMARY
    $env:GITHUB_STEP_SUMMARY = Join-Path $sandbox 'summary.md'

    function New-FixtureRemote {
        # A bare repo with two branches whose tip contents differ, standing in for a BHoM
        # dependency that carries a change on a feature branch.
        param([string]$OwnerRepo, [string]$Prefer)

        $work = Join-Path $sandbox ('work-' + ($OwnerRepo -replace '/', '-'))
        $bare = Join-Path $remoteRoot "$OwnerRepo.git"
        New-Item -ItemType Directory -Force -Path (Split-Path $bare) | Out-Null

        git init -q --bare $bare
        git init -q $work
        Push-Location $work
        try {
            git config user.email 't@t'; git config user.name 't'
            git symbolic-ref HEAD refs/heads/develop

            Set-Content -Path 'Value.cs' -Value '// base' -Encoding utf8
            git add -A; git commit -q -m 'base'
            $baseSha = (git rev-parse HEAD).Trim()

            git checkout -q -b $Prefer
            Set-Content -Path 'Value.cs' -Value '// branch change, serialisation-affecting' -Encoding utf8
            git add -A; git commit -q -m 'branch'
            $branchSha = (git rev-parse HEAD).Trim()

            git remote add origin $bare
            git push -q origin develop $Prefer
            # HEAD on the bare repo decides the remote-default fallback.
            git --git-dir=$bare symbolic-ref HEAD refs/heads/develop
        }
        finally { Pop-Location }

        return @{ Base = $baseSha; Branch = $branchSha }
    }

    function Invoke-Resolver {
        # Reproduces exactly what resolve-dependencies/action.yml does around the script:
        # create deps/, truncate _shas.txt (New-Item -ItemType File -Force at :83), set
        # PR_BRANCH and BASE_BRANCH from the event, then invoke from the workspace root.
        param(
            [string]$Workspace,
            [string]$CloneRoot,
            [AllowNull()][string]$Prefer,
            [string]$Fallback,
            # Return the script's host output instead of discarding it, for the tests that
            # assert on the workflow-command lines it emits.
            [switch]$Capture,
            # Mirrors resolve-dependencies/action.yml's PREFER_BRANCH_EXPLICIT: whether
            # PR_BRANCH came from the prefer_branch input or from the event payload.
            [switch]$Explicit
        )

        New-Item -ItemType Directory -Force -Path (Join-Path $Workspace 'deps') | Out-Null
        New-Item -ItemType File -Force -Path (Join-Path $Workspace 'deps/_shas.txt') | Out-Null

        $env:PR_BRANCH              = $Prefer
        $env:BASE_BRANCH            = $Fallback
        $env:DEP_TOKEN              = ''
        $env:PREFER_BRANCH_EXPLICIT = if ($Explicit) { 'true' } else { 'false' }

        Push-Location $Workspace
        try {
            if ($Capture) {
                return @(& $resolver -DepsFile 'dependencies.txt' -Mode 'caller' -Seeds '' `
                                     -AdditionalSeeds '' -CloneRoot $CloneRoot 6>&1)
            }
            & $resolver -DepsFile 'dependencies.txt' -Mode 'caller' -Seeds '' `
                        -AdditionalSeeds '' -CloneRoot $CloneRoot | Out-Null
        }
        finally { Pop-Location }
    }

    function Get-CheckedOutSha {
        param([string]$CloneRoot, [string]$Name)
        Push-Location (Join-Path $CloneRoot $Name)
        try { return (git rev-parse HEAD).Trim() } finally { Pop-Location }
    }

    function Get-RecordedSha {
        param([string]$Workspace, [string]$OwnerRepo)
        $line = Get-Content (Join-Path $Workspace 'deps/_shas.txt') |
                Where-Object { $_ -like "$OwnerRepo *" } | Select-Object -First 1
        if (-not $line) { return $null }
        return $line.Split(' ')[1]
    }

    function New-Workspace {
        param([string]$Name, [string]$Dependency)
        $ws = Join-Path $sandbox $Name
        New-Item -ItemType Directory -Force -Path $ws | Out-Null
        Set-Content -Path (Join-Path $ws 'dependencies.txt') -Value $Dependency -Encoding utf8
        return $ws
    }
}

AfterAll {
    git config --global --unset $insteadOfKey 2>$null
    $env:GITHUB_STEP_SUMMARY = $savedSummary
    Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

Describe 'ci-serialisation baseline reuse (register item 4)' {

    BeforeAll {
        $script:dep    = 'Fake/Dep'
        $script:prefer = 'feature/item4-demo'
        $script:shas   = New-FixtureRemote -OwnerRepo $dep -Prefer $prefer
    }

    Context 'link 1: the branch leg honours PR_BRANCH when the dependency has that branch' {

        It 'checks the dependency out at the branch tip, not the base tip' {
            $ws   = New-Workspace -Name 'link1-ws'   -Dependency $dep
            $root = Join-Path $sandbox 'link1-clones'

            Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer $prefer -Fallback 'develop'

            Get-CheckedOutSha -CloneRoot $root -Name 'Dep' | Should -Be $shas.Branch
            Get-RecordedSha   -Workspace $ws  -OwnerRepo $dep | Should -Be $shas.Branch
        }
    }

    Context 'link 2: a second invocation asking for the same ref is stable' {

        # Control, not a defect. Two invocations that both want the branch must agree. This
        # guards the ordinary single-meaning case against the always-re-resolve change: if the
        # fix had made repeat resolution unstable, this is what would catch it.
        It 'stays on the branch tip when both invocations prefer the branch' {
            $ws   = New-Workspace -Name 'link2-ws'   -Dependency $dep
            $root = Join-Path $sandbox 'link2-clones'

            Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer $prefer -Fallback 'develop'
            $afterFirst = Get-CheckedOutSha -CloneRoot $root -Name 'Dep'

            Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer $prefer -Fallback 'develop'
            $afterSecond = Get-CheckedOutSha -CloneRoot $root -Name 'Dep'

            $afterFirst  | Should -Be $shas.Branch
            $afterSecond | Should -Be $shas.Branch
            $afterSecond | Should -Be $afterFirst
        }

        It 'records the same SHA when both invocations prefer the same ref' {
            $ws   = New-Workspace -Name 'link2b-ws'   -Dependency $dep
            $root = Join-Path $sandbox 'link2b-clones'

            Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer $prefer -Fallback 'develop'
            $first = Get-RecordedSha -Workspace $ws -OwnerRepo $dep

            Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer $prefer -Fallback 'develop'
            $second = Get-RecordedSha -Workspace $ws -OwnerRepo $dep

            $second | Should -Be $first
            $second | Should -Be $shas.Branch
        }
    }

    Context 'link 3: the resolver can reach the base branch, but is never asked to' {

        It 'lands on the base tip when PR_BRANCH does not exist on the dependency' {
            $ws   = New-Workspace -Name 'link3-ws'   -Dependency $dep
            $root = Join-Path $sandbox 'link3-clones'

            # The common case: the PR branch name exists only on the subject repo, so
            # $Prefer misses and $Fallback wins. This is why item 4 has never been seen
            # firing: it needs a cross-repo branch pair, and most PRs are not one.
            Invoke-Resolver -Workspace $ws -CloneRoot $root `
                            -Prefer 'branch/that/exists/nowhere' -Fallback 'develop'

            Get-CheckedOutSha -CloneRoot $root -Name 'Dep' | Should -Be $shas.Base
        }

        It 'lands on the base tip on a fresh root when asked for the base explicitly' {
            $ws   = New-Workspace -Name 'link3b-ws'   -Dependency $dep
            $root = Join-Path $sandbox 'link3b-clones'

            # Proves the capability exists. resolve-dependencies simply exposes no input
            # that would let ci-serialisation's baseline leg request it.
            Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer 'develop' -Fallback 'develop'

            Get-CheckedOutSha -CloneRoot $root -Name 'Dep' | Should -Be $shas.Base
        }
    }

    Context 'link 4: an existing clone is re-resolved, which is the fix' {

        # This is the assertion the whole change exists for. Before the fix it read
        # Should -Be $shas.Branch: the already-cloned path short-circuited before any ref
        # resolution, so an explicit base-branch request was ignored and parameterising the
        # branch alone would not have helped. Both halves are now in place, so the second
        # invocation lands where it was asked to.
        It 'honours an explicit base-branch request when the clone is already present' {
            $ws   = New-Workspace -Name 'link4-ws'   -Dependency $dep
            $root = Join-Path $sandbox 'link4-clones'

            Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer $prefer -Fallback 'develop'
            Get-CheckedOutSha -CloneRoot $root -Name 'Dep' | Should -Be $shas.Branch

            # What ci-serialisation's baseline leg now does, via prefer_branch: base_ref.
            Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer 'develop' -Fallback 'develop'

            Get-CheckedOutSha -CloneRoot $root -Name 'Dep'    | Should -Be $shas.Base
            Get-RecordedSha   -Workspace $ws  -OwnerRepo $dep | Should -Be $shas.Base
        }

        It 'moves the working tree, not just the recorded SHA' {
            $ws   = New-Workspace -Name 'link4b-ws'   -Dependency $dep
            $root = Join-Path $sandbox 'link4b-clones'
            $file = Join-Path (Join-Path $root 'Dep') 'Value.cs'

            Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer $prefer -Fallback 'develop'
            (Get-Content $file -Raw) | Should -Match 'branch change'

            Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer 'develop' -Fallback 'develop'

            # Bookkeeping alone would leave the branch's source on disk and the base's SHA in
            # _shas.txt, which is worse than the original defect: the cache key would say
            # baseline while the assemblies were still built from branch code.
            (Get-Content $file -Raw) | Should -Match 'base'
            (Get-Content $file -Raw) | Should -Not -Match 'branch change'
        }

        It 'gives the two legs different cache-key inputs' {
            $ws   = New-Workspace -Name 'link4c-ws'   -Dependency $dep
            $root = Join-Path $sandbox 'link4c-clones'

            Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer $prefer -Fallback 'develop'
            $branchLegSha = Get-RecordedSha -Workspace $ws -OwnerRepo $dep

            Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer 'develop' -Fallback 'develop'
            $baselineLegSha = Get-RecordedSha -Workspace $ws -OwnerRepo $dep

            # The depsasm- key is a SHA-256 over the sorted owner/repo@sha set
            # (resolve-dependencies/action.yml). Differing recorded SHAs mean a differing key,
            # so the baseline leg now misses the branch leg's assembly cache and builds its own
            # closure. Identical SHAs across the legs were the observable signature of item 4
            # on production sandbox run 28089097355, where the key missed on the branch leg
            # and hit on the baseline leg.
            $baselineLegSha | Should -Not -Be $branchLegSha
        }
    }

    Context 'reporting: the resolved ref is recorded on every invocation' {

        It 'writes a selection line on a reused clone, naming the ref it resolved to' {
            $ws   = New-Workspace -Name 'rep-ws'   -Dependency $dep
            $root = Join-Path $sandbox 'rep-clones'

            Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer $prefer -Fallback 'develop'
            Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer 'develop' -Fallback 'develop'

            # _selection.txt is deleted at the start of every invocation, so this content is
            # the second invocation's alone. It was empty before the reporting change, which
            # is why the baseline leg's job summary had no dependency table.
            $selection = Get-Content (Join-Path $ws 'deps/_selection.txt')
            $selection | Should -Not -BeNullOrEmpty
            ($selection -join "`n") | Should -Match ([regex]::Escape("$dep|Dep|develop|"))
        }

        It 'updates the marker so a later invocation reports the current ref' {
            $ws   = New-Workspace -Name 'rep2-ws'   -Dependency $dep
            $root = Join-Path $sandbox 'rep2-clones'

            Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer $prefer -Fallback 'develop'
            $marker = Join-Path $root '_selected-refs.txt'
            (Get-Content $marker -Raw) | Should -Match ([regex]::Escape("Dep|$prefer"))

            Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer 'develop' -Fallback 'develop'
            (Get-Content $marker -Raw) | Should -Match ([regex]::Escape('Dep|develop'))
            (Get-Content $marker -Raw) | Should -Not -Match ([regex]::Escape($prefer))
        }

        # The guard distinguishes who asked. An explicit prefer_branch that moved nothing is a
        # misconfiguration worth annotating; the same outcome from an inherited event default is
        # the ordinary case and must not annotate, because it happens on most baseline runs.
        # Never an error either way.
        It 'warns when prefer_branch was explicit and nothing moved' {
            $ws   = New-Workspace -Name 'guard-ws'   -Dependency $dep
            $root = Join-Path $sandbox 'guard-clones'

            Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer $prefer -Fallback 'develop'
            $second = Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer $prefer -Fallback 'develop' -Explicit -Capture

            ($second -join "`n") | Should -Match '::warning title=resolve-dependencies::prefer_branch was set explicitly'
            ($second -join "`n") | Should -Match 'register item 4'
        }

        It 'stays silent, with no annotation, when the default was inherited and nothing moved' {
            $ws   = New-Workspace -Name 'guard1b-ws'   -Dependency $dep
            $root = Join-Path $sandbox 'guard1b-clones'

            Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer $prefer -Fallback 'develop'
            $second = Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer $prefer -Fallback 'develop' -Capture

            # The case that made the first version of this guard useless: it fired here, which is
            # most baseline runs. Informational only now.
            ($second -join "`n") | Should -Not -Match '::warning'
            ($second -join "`n") | Should -Match 'inherited from the event rather than requested'
        }

        It 'does not warn on a first invocation, which has nothing to compare against' {
            $ws   = New-Workspace -Name 'guard2-ws'   -Dependency $dep
            $root = Join-Path $sandbox 'guard2-clones'

            $first = Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer $prefer -Fallback 'develop' -Explicit -Capture

            ($first -join "`n") | Should -Not -Match '::warning'
            ($first -join "`n") | Should -Not -Match 'Repeat resolution'
        }

        It 'reports a notice, not a warning, when a repeat resolution did move' {
            $ws   = New-Workspace -Name 'guard3-ws'   -Dependency $dep
            $root = Join-Path $sandbox 'guard3-clones'

            Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer $prefer -Fallback 'develop'
            $second = Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer 'develop' -Fallback 'develop' -Explicit -Capture

            ($second -join "`n") | Should -Match 'Repeat resolution moved 1 of 1'
            ($second -join "`n") | Should -Not -Match '::warning'
        }

        It 'keeps the marker file out of the directories the caller junctions' {
            $ws   = New-Workspace -Name 'rep3-ws'   -Dependency $dep
            $root = Join-Path $sandbox 'rep3-clones'

            Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer $prefer -Fallback 'develop'

            # The calling action junctions every DIRECTORY under the clone root into the
            # workspace parent. A marker stored as a directory would be linked in as though it
            # were a dependency.
            Get-ChildItem $root -Directory | ForEach-Object { $_.Name } | Should -Be @('Dep')
        }
    }
}
