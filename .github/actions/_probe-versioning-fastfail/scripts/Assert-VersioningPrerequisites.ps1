[CmdletBinding()]
param(
    # IncludedDLLs.txt from the baseline installer's latest published release.
    [Parameter(Mandatory)][string]$BaselineFile,
    # Folder produced by dependency resolution + primary build.
    [Parameter(Mandatory)][string]$AssembliesDir,
    # ACMRT change list (deletions excluded) written by compute-changed-files.
    # An existing-but-empty file means every matched change in the PR was a
    # deletion. Absent (non-PR run) or non-empty means apply the fast-fail.
    [string]$ChangedListPath = '',
    # Skip the fast-fail and run versioning regardless. Mirrors BHoMBot's
    # -force: used when a PR legitimately reduces the produced DLL set.
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# Filter to canonical BHoM DLLs. Third-party shared deps (Newtonsoft.Json, WiX
# bundles etc.) legitimately vary between builds; the canonical set is what
# versioning genuinely depends on. Same regex intent as BHoMBot's Versioning.cs:187.
$canonicalRegex = '(?i)(oM|_Engine|_Adapter)\.dll$|(?i)Revit_.+20\d{2}\.dll$'

$expected = Get-Content $BaselineFile |
    ForEach-Object { [System.IO.Path]::GetFileName($_.Trim()) } |
    Where-Object { $_ -match $canonicalRegex } |
    Sort-Object -Unique

$present = @(Get-ChildItem $AssembliesDir -Filter '*.dll' -ErrorAction SilentlyContinue |
             ForEach-Object { $_.Name })

$missing = @($expected | Where-Object { $_ -notin $present })

if ($missing.Count -eq 0) {
    Write-Host "::notice::Versioning prerequisites present ($($expected.Count) canonical DLLs matched from baseline)."
    exit 0
}

$missingList = $missing -join ', '

if ($Force) {
    Write-Host "::warning::Versioning prerequisites missing ($missingList), but force=true, so running versioning anyway."
    exit 0
}

# Deletion-only guard: mirrors BHoMBot Versioning.cs:209 (csFiles.Any(File.Exists)).
# A PR that only removes source legitimately produces fewer DLLs, so a missing
# DLL is expected: run versioning rather than blocking it here. The check applies
# only when the change list exists (PR run) and is empty (all matched changes
# were deletions); an absent list (non-PR run) or a non-empty list falls through
# to the fast-fail.
if ($ChangedListPath -and (Test-Path $ChangedListPath)) {
    $nonDeleted = @(Get-Content $ChangedListPath | Where-Object { $_.Trim() })
    if ($nonDeleted.Count -eq 0) {
        Write-Host "::warning::Versioning prerequisites missing ($missingList), but this PR only deletes source files, so fewer DLLs is expected. Skipping fast-fail and running versioning."
        exit 0
    }
}

Write-Host "::error title=Versioning prerequisites missing::The following DLLs are expected (per the last published baseline release) but were not produced by dependency resolution + primary build: $missingList. Fix the upstream failure before re-running versioning."
exit 1
