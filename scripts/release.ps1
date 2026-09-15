# Release script — packs to the local folder feed, tags, and pushes.
#
# Why: after pushing a release tag, nuget.org needs minutes to index the new
# packages (plus NuGet's 30-minute HTTP index cache on consumer machines).
# Sibling repos (PicoActor, PicoNode, PicoAgent, ...) consume PicoInfra packages
# via PackageReference, so local development would stall until indexing
# completes. This script packs ALL packages into the local folder feed
# (NuGet.config: local -> artifacts/nupkg) before tagging, so local restores
# resolve the new version instantly. CI (release.yml) still publishes to
# nuget.org as usual; once indexed, both sources serve the same bits.
#
# Usage (from repo root):
#   ./scripts/release.ps1 -Version 2026.8.9
#   ./scripts/release.ps1 -Version 2026.8.9 -SkipTests
#   ./scripts/release.ps1 -Version 2026.8.9 -NoPush   # pack + tag only
#
# The pack phase mirrors release.yml exactly (three phases, staged
# RestoreAdditionalProjectSources), so local nupkgs match CI output.

param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [switch]$SkipTests,

    [switch]$NoPush
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Fail([string]$message) {
    Write-Error $message
    exit 1
}

# --- Preconditions -----------------------------------------------------------

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Push-Location $repoRoot
try {
    if (-not ($Version -match '^\d+\.\d+\.\d+$')) {
        Fail "Version must be numeric (e.g. 2026.8.9), got '$Version'"
    }

    $tag = "v$Version"
    if (git tag -l $tag) {
        Fail "Tag '$tag' already exists"
    }

    $dirty = git status --porcelain
    if ($dirty) {
        Fail "Working tree is not clean:`n$dirty`nCommit or stash changes before releasing."
    }

    # --- Tests ------------------------------------------------------------------

    if (-not $SkipTests) {
        Write-Host "=== Running tests ===" -ForegroundColor Cyan
        $testProjects = @(
            "PicoDI/tests/PicoDI.Test/PicoDI.Test.csproj",
            "PicoCfg/tests/PicoCfg.Tests/PicoCfg.Tests.csproj",
            "PicoCfg/tests/PicoCfg.Gen.Tests/PicoCfg.Gen.Tests.csproj",
            "PicoCfg/tests/PicoCfg.DI.Tests/PicoCfg.DI.Tests.csproj",
            "PicoCfg/tests/PicoCfg.Json.Tests/PicoCfg.Json.Tests.csproj",
            "PicoCfg/tests/PicoCfg.Yaml.Tests/PicoCfg.Yaml.Tests.csproj",
            "PicoCfg/tests/PicoCfg.Ini.Tests/PicoCfg.Ini.Tests.csproj",
            "PicoCfg/tests/PicoCfg.Toml.Tests/PicoCfg.Toml.Tests.csproj",
            "PicoLog/tests/PicoLog.Tests/PicoLog.Tests.csproj",
            "PicoLog/tests/PicoLog.Json.Tests/PicoLog.Json.Tests.csproj",
            "PicoAop/tests/PicoAop.Tests/PicoAop.Tests.csproj",
            "PicoMediator/tests/PicoMediator.Tests/PicoMediator.Tests.csproj"
        )
        foreach ($project in $testProjects) {
            Write-Host "  -> $project" -ForegroundColor DarkGray
            dotnet test --project $project --configuration Release
            if ($LASTEXITCODE -ne 0) { Fail "Tests failed: $project" }
        }
    }

    # --- Pack (mirrors release.yml phase ordering) --------------------------------

    $nupkgDir = Join-Path $repoRoot "artifacts/nupkg"
    Remove-Item -Path $nupkgDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $nupkgDir | Out-Null

    $packCommon = @(
        "--configuration", "Release",
        "--output", $nupkgDir,
        "-p:Version=$Version"
    )

    function Pack([string]$project, [bool]$useProjectReferences, [bool]$stagedSource = $false) {
        $args = @($packCommon + @("-p:UseProjectReferences=$useProjectReferences"))
        if ($stagedSource) {
            $args += "-p:RestoreAdditionalProjectSources=$nupkgDir"
        }
        Write-Host "  -> pack $project" -ForegroundColor DarkGray
        dotnet pack $project @args
        if ($LASTEXITCODE -ne 0) { Fail "Pack failed: $project" }
    }

    Write-Host "=== Phase 1: Abstractions (bundled generators, project references) ===" -ForegroundColor Cyan
    Pack "PicoSchedule/src/PicoSchedule/PicoSchedule.csproj" $true   # standalone, zero dependencies
    Pack "PicoDI/src/PicoDI.Abs/PicoDI.Abs.csproj" $true
    Pack "PicoAop/src/PicoAop.Abs/PicoAop.Abs.csproj" $true $true
    Pack "PicoMediator/src/PicoMediator.Abs/PicoMediator.Abs.csproj" $true
    Pack "PicoCfg/src/PicoCfg.Abs/PicoCfg.Abs.csproj" $true
    Pack "PicoLog/src/PicoLog.Abs/PicoLog.Abs.csproj" $true

    Write-Host "=== Phase 2: Consumer libraries (NuGet dependencies on Phase 1) ===" -ForegroundColor Cyan
    Pack "PicoAop/src/PicoAop.DI/PicoAop.DI.csproj" $false $true
    Pack "PicoMediator/src/PicoMediator/PicoMediator.csproj" $false $true
    Pack "PicoMediator/src/PicoMediator.DI/PicoMediator.DI.csproj" $false $true
    Pack "PicoDI/src/PicoDI/PicoDI.csproj" $false $true
    Pack "PicoCfg/src/PicoCfg/PicoCfg.csproj" $false $true
    Pack "PicoCfg/src/PicoCfg.DI/PicoCfg.DI.csproj" $false $true

    Write-Host "=== Phase 3: SerDe + remaining consumers ===" -ForegroundColor Cyan
    Pack "PicoCfg/src/PicoCfg.Json/PicoCfg.Json.csproj" $false $true
    Pack "PicoCfg/src/PicoCfg.Yaml/PicoCfg.Yaml.csproj" $false $true
    Pack "PicoCfg/src/PicoCfg.Ini/PicoCfg.Ini.csproj" $false $true
    Pack "PicoCfg/src/PicoCfg.Toml/PicoCfg.Toml.csproj" $false $true
    Pack "PicoLog/src/PicoLog/PicoLog.csproj" $false $true
    Pack "PicoLog/src/PicoLog.DI/PicoLog.DI.csproj" $false $true
    Pack "PicoLog/src/PicoLog.Json/PicoLog.Json.csproj" $false $true
    Pack "PicoAop/src/PicoAop/PicoAop.csproj" $false $true

    $packed = @(Get-ChildItem $nupkgDir -Filter "*.nupkg")
    if ($packed.Count -eq 0) { Fail "No packages were produced" }
    Write-Host "=== Local feed ready: $nupkgDir ($($packed.Count) packages, version $Version) ===" -ForegroundColor Green

    # --- Tag + push ----------------------------------------------------------------

    git tag -a $tag -m "PicoInfra $Version - packed locally + published via release.yml"
    if ($LASTEXITCODE -ne 0) { Fail "git tag failed" }

    if (-not $NoPush) {
        git push origin main
        if ($LASTEXITCODE -ne 0) { Fail "git push main failed" }
        git push origin $tag
        if ($LASTEXITCODE -ne 0) { Fail "git push tag failed" }
    }
    else {
        Write-Host "Tag '$tag' created locally. Push when ready: git push origin main $tag" -ForegroundColor Yellow
    }

    Write-Host "=== Release $Version complete ===" -ForegroundColor Green
}
finally {
    Pop-Location
}
