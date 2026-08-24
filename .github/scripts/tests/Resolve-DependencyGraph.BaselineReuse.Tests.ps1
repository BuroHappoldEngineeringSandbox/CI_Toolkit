# Resolve-DependencyGraph.BaselineReuse.Tests.ps1
#
# Demonstrates findings-register item 4: ci-serialisation's baseline leg is built against the
# BRANCH's dependency code, so a regression introduced on the dependency side appears in both
# legs, compares equal, and the check passes.
#
# Written as a DEMONSTRATION, not a specification. Every assertion here describes what the
# resolver does today. Item 4 is a correctness finding on a check under a standing gate, and
# the fix is behaviour-changing and unscoped, so nothing here asserts a preferred behaviour.
# Assertions expected to invert once item 4 is fixed are marked INVERTS-ON-ITEM4.
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
#   4. Even if a caller COULD ask for the base branch on the second invocation, the
#      already-cloned shortcut would ignore it. Two independent causes, so a fix addressing
#      only one of them does not work. This is the assertion that matters most.
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
            [string]$Fallback
        )

        New-Item -ItemType Directory -Force -Path (Join-Path $Workspace 'deps') | Out-Null
        New-Item -ItemType File -Force -Path (Join-Path $Workspace 'deps/_shas.txt') | Out-Null

        $env:PR_BRANCH   = $Prefer
        $env:BASE_BRANCH = $Fallback
        $env:DEP_TOKEN   = ''

        Push-Location $Workspace
        try {
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

    Context 'link 2: the baseline leg reuses the branch leg clone' {

        It 'does not move the dependency off the branch tip on a second invocation' {
            $ws   = New-Workspace -Name 'link2-ws'   -Dependency $dep
            $root = Join-Path $sandbox 'link2-clones'

            # Branch leg.
            Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer $prefer -Fallback 'develop'
            $afterBranchLeg = Get-CheckedOutSha -CloneRoot $root -Name 'Dep'

            # Baseline leg. PR_BRANCH is unchanged because resolve-dependencies sources it
            # from the event payload, which does not vary within a job. ci-serialisation
            # clears ProgramData\BHoM\Assemblies between the legs but never the clone root,
            # so this is what its second resolve sees.
            Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer $prefer -Fallback 'develop'
            $afterBaselineLeg = Get-CheckedOutSha -CloneRoot $root -Name 'Dep'

            $afterBranchLeg   | Should -Be $shas.Branch
            # INVERTS-ON-ITEM4: a corrected baseline leg would report $shas.Base here.
            $afterBaselineLeg | Should -Be $shas.Branch
            $afterBaselineLeg | Should -Be $afterBranchLeg
        }

        It 'records the same SHA both times, so the assembly cache key is identical' {
            $ws   = New-Workspace -Name 'link2b-ws'   -Dependency $dep
            $root = Join-Path $sandbox 'link2b-clones'

            Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer $prefer -Fallback 'develop'
            $first = Get-RecordedSha -Workspace $ws -OwnerRepo $dep

            Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer $prefer -Fallback 'develop'
            $second = Get-RecordedSha -Workspace $ws -OwnerRepo $dep

            # The depsasm- cache key is a SHA-256 over the sorted owner/repo@sha set
            # (resolve-dependencies/action.yml:145-164). Identical recorded SHAs therefore
            # mean an identical key, which is why the baseline leg restores the branch leg's
            # assemblies instead of building its own. Observed on production sandbox run
            # 28089097355: key depsasm-Windows-Release-354c6cb2... missed on the branch leg
            # and hit on the baseline leg, with 5 clones the first time and 0 the second.
            $second | Should -Be $first
            # INVERTS-ON-ITEM4.
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

    Context 'link 4: the two causes are independent' {

        It 'ignores an explicit base-branch request when the clone is already present' {
            $ws   = New-Workspace -Name 'link4-ws'   -Dependency $dep
            $root = Join-Path $sandbox 'link4-clones'

            # Branch leg, as normal.
            Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer $prefer -Fallback 'develop'
            Get-CheckedOutSha -CloneRoot $root -Name 'Dep' | Should -Be $shas.Branch

            # Now ask for the base branch on the second invocation, i.e. pretend the caller
            # had been given the per-leg parameter it currently lacks. The already-cloned
            # path at :91 short-circuits before any ref resolution, so the request is ignored.
            Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer 'develop' -Fallback 'develop'

            # THE ASSERTION THAT MATTERS: parameterising the branch alone would not fix
            # item 4. The clone reuse has to be addressed too. INVERTS-ON-ITEM4.
            Get-CheckedOutSha -CloneRoot $root -Name 'Dep' | Should -Be $shas.Branch
            Get-RecordedSha   -Workspace $ws  -OwnerRepo $dep | Should -Be $shas.Branch
        }
    }
}
