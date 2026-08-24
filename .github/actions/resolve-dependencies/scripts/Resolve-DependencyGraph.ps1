param(
    [string]$DepsFile         = "dependencies.txt",
    [string]$Mode             = "caller",   # "caller" | "seeds"
    [string]$Seeds            = "",         # used only when Mode = "seeds"
    [string]$AdditionalSeeds  = "",         # used only when Mode = "caller"; appended after caller graph
    [string]$CloneRoot        = "C:\bhom-deps"  # isolated clone root, outside any workspace
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root       = (Get-Location).Path
$depsDir    = Join-Path $root "deps"        # metadata only (_shas, _order, _selection)
$cloneRoot  = $CloneRoot
$shaFile    = Join-Path $depsDir "_shas.txt"
$orderOut   = Join-Path $depsDir "_order.txt"
$selectFile = Join-Path $depsDir "_selection.txt"

# Which ref each clone was last checked out to, kept beside the clones rather than in deps/
# because the action truncates _shas.txt and deletes _selection.txt on every invocation while
# the clone root persists for the whole job. A flat file, not a directory: the junction loop at
# the end of the calling action enumerates directories under the clone root and would otherwise
# link this into the workspace parent as if it were a dependency.
$refMarkerFile = Join-Path $cloneRoot "_selected-refs.txt"

New-Item -ItemType Directory -Force -Path $cloneRoot | Out-Null

if (Test-Path $selectFile) { Remove-Item $selectFile -Force }

# Parses owner/repo or owner/repo@ref lines; rejects blank, comment, and malformed entries.
function Lines([string]$path) {
    if (Test-Path $path) {
        return Get-Content $path |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_ -ne "" -and -not $_.StartsWith("#") } |
            ForEach-Object {
                if ($_.IndexOfAny(@(' ', "`t")) -ge 0) {
                    throw "Malformed dependency entry (contains whitespace): '$_'. Each line must be a single 'owner/repo' or 'owner/repo@ref'."
                }
                $_
            } |
            ForEach-Object {
                if (-not ($_ -match "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(@[A-Za-z0-9._/-]+)?$")) {
                    throw "Malformed dependency entry (invalid format): '$_'. Expected 'owner/repo' or 'owner/repo@ref' using alphanumeric characters, dots, underscores, or hyphens."
                }
                $_
            }
    }
    return @()
}

function Parse-RepoSpec([string]$spec) {
    $ref = $null
    if ($spec.Contains("@")) {
        $parts = $spec.Split("@",2)
        $spec  = $parts[0].Trim()
        $ref   = $parts[1].Trim()
    }
    return @{ Key=$spec; Ref=$ref }
}

# Resolution order: explicit @ref → PR head branch → base branch → develop → remote default.
# $Prefer is null on push/dispatch events, skipping the ls-remote probe.
$Prefer   = if ([string]::IsNullOrWhiteSpace($env:PR_BRANCH))   { $null } else { $env:PR_BRANCH.Trim() }
$Fallback = if ([string]::IsNullOrWhiteSpace($env:BASE_BRANCH)) { 'develop' } else { $env:BASE_BRANCH.Trim() }

$cloned     = New-Object System.Collections.Generic.HashSet[string]
$visited    = New-Object System.Collections.Generic.HashSet[string]  # guards against circular deps
$nameMap    = @{}  # owner/repo -> folder
$pathMap    = @{}  # owner/repo -> path
$folderUsed = @{}  # folder name -> owner/repo, for collision detection

# Use insteadOf to inject the token at the git config level — keeps it out of command
# arguments, process listings, and cloned repos' .git/config. Removed in a finally block
# to avoid persisting the credential on self-hosted runners even when the script errors.
$tokenInsteadOfKey = $null
if (-not [string]::IsNullOrWhiteSpace($env:DEP_TOKEN)) {
    $tokenInsteadOfKey = "url.https://x-access-token:$($env:DEP_TOKEN)@github.com/.insteadOf"
    git config --global $tokenInsteadOfKey "https://github.com/"
}

try {

function Get-FolderName([string]$ownerRepo) {
    $name = $ownerRepo.Split("/")[1]
    if ($folderUsed.ContainsKey($name) -and $folderUsed[$name] -ne $ownerRepo) {
        throw "Repository name collision: '$name' is claimed by both '$($folderUsed[$name])' and '$ownerRepo'. Rename one repo or use an explicit @ref to disambiguate."
    }
    $folderUsed[$name] = $ownerRepo
    return $name
}

# A detached HEAD cannot report the branch it came from, so the ref each clone was checked out
# to is recorded here and read back on a later invocation. Without it a reused clone can only be
# described by its SHA, which does not tell a reader whether the ref was the one this leg asked
# for. See the reporting note on the reuse path in Clone-And-Checkout.
function Set-SelectedRefMarker([string]$name, [string]$ref) {
    $kept = @()
    if (Test-Path $refMarkerFile) {
        $kept = @(Get-Content $refMarkerFile |
                  Where-Object { $_ -notmatch "^$([regex]::Escape($name))\|" })
    }
    ($kept + "$name|$ref") | Set-Content -Path $refMarkerFile -Encoding utf8
}

function Get-SelectedRefMarker([string]$name) {
    if (-not (Test-Path $refMarkerFile)) { return $null }
    $line = Get-Content $refMarkerFile |
            Where-Object { $_ -match "^$([regex]::Escape($name))\|" } |
            Select-Object -First 1
    if (-not $line) { return $null }
    return $line.Split('|', 2)[1]
}

function Clone-And-Checkout([string]$ownerRepo, [string]$ref) {

    $name = Get-FolderName $ownerRepo
    $path = Join-Path $cloneRoot $name

    if (-not (Test-Path (Join-Path $path ".git"))) {

        git clone "https://github.com/$ownerRepo.git" $path --no-tags --depth 1 | Out-Null

        $selectedRef = $null
        Push-Location $path
        try {
            $used = $false

            if ($ref) {
                $hasHead = git ls-remote --heads origin $ref
                $hasTag  = git ls-remote --tags  origin $ref
                if ($hasHead -or $hasTag) {
                    git fetch origin $ref --depth 1 | Out-Null
                    git checkout -q FETCH_HEAD
                    if ($LASTEXITCODE -ne 0) { throw "git checkout FETCH_HEAD failed for '$ownerRepo' (ref=$ref)" }
                    $selectedRef = $ref
                    $used = $true
                } else {
                    Write-Warning "Explicit ref '$ref' not found on '$ownerRepo' — falling back."
                }
            }

            if (-not $used) {
                $hasPrefer   = if ($Prefer)   { git ls-remote --heads origin $Prefer }   else { $null }
                $hasFallback = git ls-remote --heads origin $Fallback
                if ($hasPrefer) {
                    git fetch origin $Prefer --depth 1 | Out-Null
                    git checkout -q FETCH_HEAD
                    if ($LASTEXITCODE -ne 0) { throw "git checkout FETCH_HEAD failed for '$ownerRepo' (ref=$Prefer)" }
                    $selectedRef = $Prefer
                } elseif ($hasFallback) {
                    git fetch origin $Fallback --depth 1 | Out-Null
                    git checkout -q FETCH_HEAD
                    if ($LASTEXITCODE -ne 0) { throw "git checkout FETCH_HEAD failed for '$ownerRepo' (ref=$Fallback)" }
                    $selectedRef = $Fallback
                } else {
                    # Neither PR nor base branch exists on this dep; fall back to remote default.
                    git fetch origin HEAD --depth 1 | Out-Null
                    git checkout -q FETCH_HEAD
                    if ($LASTEXITCODE -ne 0) { throw "git checkout FETCH_HEAD failed for '$ownerRepo' (remote default)" }
                    $defaultRef = (git ls-remote --symref origin HEAD |
                        Select-String 'ref: refs/heads/(\S+)\s+HEAD' |
                        ForEach-Object { $_.Matches[0].Groups[1].Value } |
                        Select-Object -First 1)
                    $selectedRef = if ($defaultRef) { $defaultRef } else { "(remote default)" }
                }
            }

            $sha = (git rev-parse HEAD).Trim()
            Add-Content -Path $shaFile    -Value "$ownerRepo $sha"
            Add-Content -Path $selectFile -Value "$ownerRepo|$name|$selectedRef|$sha"
            Set-SelectedRefMarker $name $selectedRef
        }
        finally {
            Pop-Location
        }

        # Write-Host, not ::notice. A ::notice with no file= becomes a check-run
        # annotation attributed to ".github" at the log line number, and there is one
        # per dependency: nine on a typical ci-build run, competing with the caller's
        # own diagnostics for GitHub's per-step annotation cap. The same information
        # already reaches the job summary as a table at the end of this script, which
        # is where a reader looks for it.
        Write-Host "Dependency checkout: $ownerRepo -> $selectedRef @ $($sha.Substring(0,7))"
    }
    else {
        # Repo already cloned (e.g. baseline run re-uses branch-build clones).
        # _shas.txt is reset between invocations, so write the SHA even though we skip re-cloning.
        # Without this the cache-key computation produces an empty keypart, skipping both the
        # assembly cache restore and the dep build, leaving C:\ProgramData\BHoM\Assemblies empty.
        Push-Location $path
        try {
            $sha = (git rev-parse HEAD).Trim()
            Add-Content -Path $shaFile -Value "$ownerRepo $sha"

            # Report the reuse rather than staying silent about it. _selection.txt is deleted on
            # every invocation and was previously written only when a clone was created, so a
            # second invocation in the same job produced no selection table at all and its log
            # said nothing about which refs it was actually building. That silence is what let
            # ci-serialisation's baseline leg look like it had resolved its own dependencies.
            # The ref comes from the marker because HEAD here is detached at FETCH_HEAD and
            # cannot name the branch it came from.
            $reusedRef = Get-SelectedRefMarker $name
            if (-not $reusedRef) { $reusedRef = "(unrecorded)" }
            Add-Content -Path $selectFile -Value "$ownerRepo|$name|$reusedRef (reused)|$sha"
            Write-Host "Dependency reused: $ownerRepo -> $reusedRef @ $($sha.Substring(0,7)) (clone already present, not re-resolved)"
        }
        finally {
            Pop-Location
        }
    }

    $nameMap[$ownerRepo] = $name
    $pathMap[$ownerRepo] = $path

    return @{ Key=$ownerRepo; Name=$name; Path=$path }
}

# Depth-first expansion: children first, then self. Returns List[hashtable] { Key, Name, Path }.
function Build-Chain([string]$ownerRepo, [string]$ref=$null) {

    $chain = New-Object System.Collections.Generic.List[hashtable]

    if (-not $cloned.Contains($ownerRepo)) {
        $cloned.Add($ownerRepo) | Out-Null
        Clone-And-Checkout $ownerRepo $ref | Out-Null
    }

    # Already visited: skip re-expansion to prevent infinite recursion on circular deps.
    if ($visited.Contains($ownerRepo)) {
        if ($pathMap.ContainsKey($ownerRepo)) {
            $chain.Add(@{ Key=$ownerRepo; Name=$nameMap[$ownerRepo]; Path=$pathMap[$ownerRepo] }) | Out-Null
        }
        return $chain
    }
    $visited.Add($ownerRepo) | Out-Null

    $repoPath = $pathMap[$ownerRepo]
    $depsFileLocal = Join-Path $repoPath "dependencies.txt"

    if (Test-Path $depsFileLocal) {
        foreach ($line in (Lines $depsFileLocal)) {
            $parsed = Parse-RepoSpec $line
            $childList = Build-Chain $parsed.Key $parsed.Ref
            foreach ($c in $childList) { $chain.Add($c) | Out-Null }
        }
    }

    $chain.Add(@{ Key=$ownerRepo; Name=$nameMap[$ownerRepo]; Path=$pathMap[$ownerRepo] }) | Out-Null

    return $chain
}

$phaseList = New-Object System.Collections.Generic.List[string]
$phaseSet  = New-Object System.Collections.Generic.HashSet[string]

if ($Mode -eq "seeds") {

    Write-Host "----- MODE: seeds (build only specified seed repo(s) + dependencies) -----"

    if (-not [string]::IsNullOrWhiteSpace($Seeds)) {
        $seedList = @($Seeds.Trim().Split("`n")) |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_ -ne "" }

        foreach ($s in $seedList) {
            if ($s.IndexOfAny(@(' ', "`t")) -ge 0) {
                throw "Malformed seed entry (contains whitespace): '$s'. Each entry must be 'owner/repo' or 'owner/repo@ref'."
            }
            if ($s -notmatch "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(@[A-Za-z0-9._/-]+)?$") {
                throw "Malformed seed entry (invalid format): '$s'. Expected 'owner/repo' or 'owner/repo@ref'."
            }
        }

        foreach ($s in $seedList) {
            $sp = Parse-RepoSpec $s
            $chain = Build-Chain $sp.Key $sp.Ref
            foreach ($item in $chain) {
                if ($phaseSet.Add($item.Key)) { $phaseList.Add($item.Key) | Out-Null }
            }
            Write-Host "Seed: $($sp.Key)"
        }
    }
    else {
        Write-Host "(none)"
    }
}
else {

    Write-Host "----- MODE: caller (build caller repo's dependencies from $DepsFile) -----"

    foreach ($seed in (Lines $DepsFile)) {
        $parsed = Parse-RepoSpec $seed
        $chain  = Build-Chain $parsed.Key $parsed.Ref
        foreach ($item in $chain) {
            if ($phaseSet.Add($item.Key)) { $phaseList.Add($item.Key) | Out-Null }
        }
        Write-Host "Dep: $($parsed.Key)"
    }

    if ($phaseList.Count -eq 0) {
        Write-Host "(none)"
    }
}

# Additional seeds (caller mode only): appended after the caller graph so the caller's dependency assemblies build first.
if ($Mode -ne "seeds" -and -not [string]::IsNullOrWhiteSpace($AdditionalSeeds)) {
    Write-Host "----- Additional seeds (appended to caller graph) -----"

    $extraList = @($AdditionalSeeds.Trim().Split("`n")) |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -ne "" }

    foreach ($s in $extraList) {
        if ($s.IndexOfAny(@(' ', "`t")) -ge 0) {
            throw "Malformed additional-seed entry (contains whitespace): '$s'. Each entry must be 'owner/repo' or 'owner/repo@ref'."
        }
        if ($s -notmatch "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(@[A-Za-z0-9._/-]+)?$") {
            throw "Malformed additional-seed entry (invalid format): '$s'. Expected 'owner/repo' or 'owner/repo@ref'."
        }
    }

    foreach ($s in $extraList) {
        $sp    = Parse-RepoSpec $s
        $chain = Build-Chain $sp.Key $sp.Ref
        foreach ($item in $chain) {
            if ($phaseSet.Add($item.Key)) { $phaseList.Add($item.Key) | Out-Null }
        }
        Write-Host "  Extra seed: $($sp.Key)"
    }
}

$merged = $phaseList

# Always write _order.txt even when empty — Build-Dependencies.ps1 relies on it existing.
if ($merged.Count -gt 0) {
    $merged | Set-Content -Path $orderOut -Encoding utf8
} else {
    [string]::Empty | Set-Content -Path $orderOut -Encoding utf8
}

Write-Host "== Final build order (owner/repo) =="
if ($merged.Count -gt 0) {
    Get-Content $orderOut | ForEach-Object { Write-Host "  $_ → $cloneRoot\$($_.Split('/')[1])" }
} else {
    Write-Host "  (no dependencies)"
}

if (Test-Path $selectFile) {
    Write-Host ""
    Write-Host "== Checkout selections =="

    $mdLines = @("### Dependency graph checkout selections", "",
                 "| Repository | Folder | Branch | SHA |",
                 "|---|---|---|---|")

    foreach ($line in (Get-Content $selectFile)) {
        $t = $line.Split("|")
        if ($t.Length -ge 4) {
            $shortSha = if ($t[3].Length -ge 7) { $t[3].Substring(0,7) } else { $t[3] }
            Write-Host "  $($t[0]) → $($t[2]) @ $shortSha"
            $mdLines += "| ``$($t[0])`` | $($t[1]) | $($t[2]) | ``$shortSha`` |"
        }
    }

    if ($env:GITHUB_STEP_SUMMARY) {
        $mdLines | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Encoding utf8 -Append
    }
}

} finally {
    # Remove the credential rewrite — on self-hosted runners ~/.gitconfig persists between
    # jobs and would leak the short-lived token to subsequent jobs. The finally block ensures
    # cleanup runs even when the script exits via a thrown exception.
    if ($null -ne $tokenInsteadOfKey) {
        git config --global --unset $tokenInsteadOfKey 2>$null
        Write-Host "::debug::Removed git credential rewrite from global config."
    }
}
