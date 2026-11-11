# PTMagic 2.10: required offline Phase 2 development verification.
# No configured runtime, ProfitTrailer writes, or browser acceptance.
$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..\..")).Path
Push-Location -LiteralPath $RepoRoot
try {
    function Assert-NativeExit([string]$Step) {
        if ($LASTEXITCODE -ne 0) { throw "$Step failed (exit $LASTEXITCODE)" }
    }

    git switch phase1/market-data-foundation
    Assert-NativeExit "git switch"
    git pull --ff-only
    Assert-NativeExit "git pull"

    $Source = (& git rev-parse HEAD).Trim()
    Assert-NativeExit "git rev-parse HEAD"
    $Changes = @(& git status --porcelain)
    Assert-NativeExit "git status"
    Write-Host "Source revision: $Source"
    if ($Changes.Count -gt 0) {
        Write-Warning "Working tree contains local modifications; record source as $Source-dirty."
    }

    dotnet --version
    Assert-NativeExit "dotnet --version"
    dotnet build .\PTMagic.sln
    Assert-NativeExit "dotnet build"

    $Checks = @(
        "Phase2Funding",
        "Phase2OpenInterest",
        "Phase2OpenInterestIncremental",
        "Phase2LongShortRatios",
        "Phase2AdaptiveAcquisition",
        "Phase1AReadiness",
        "Phase1AnalyticsReliability",
        "Phase1COhlcv",
        "Phase1DCorrelation",
        "Phase1MetricWarnings",
        "Phase1SmsSummaryIsolation",
        "Phase1TriggerLogic",
        "NormalizedPriceTrendMigration",
        "NormalizedWarmStartCache",
        "TriggerConfigurationValidation",
        "MonitorSettingsRoundTrip",
        "ConfigurationLifecycle",
        "DecisionHistory"
    )
    foreach ($Name in $Checks) {
        $Project = ".\Tests\$Name\$Name.csproj"
        if (-not (Test-Path -LiteralPath $Project -PathType Leaf)) {
            throw "Missing test project: $Project"
        }
        Write-Host ""
        Write-Host "=== $Name ==="
        dotnet run --project $Project
        Assert-NativeExit "$Name harness"
    }

    Write-Host ""
    Write-Host "PASS: Solution build and all $($Checks.Count) offline harnesses."
    Write-Host "Verified source: $Source"
}
finally {
    Pop-Location
}
