# Regression gate for the benchmarks (#16): compares the BenchmarkDotNet CSV reports with
# tests/Kedo.Benchmarks/baseline.json. Allocations are deterministic, so more than 10% over the baseline fails;
# time on shared runners is noisy, so only more than 3x the baseline fails.
# Usage: pwsh tools/bench-gate.ps1 [-Results <dir>] [-Update]   (-Update rewrites the baseline from the results)
param(
    [string]$Results = 'BenchmarkDotNet.Artifacts/results',
    [string]$Baseline = (Join-Path (Split-Path -Parent $PSScriptRoot) 'tests/Kedo.Benchmarks/baseline.json'),
    [double]$AllocationTolerance = 0.10,
    [double]$TimeFactor = 3.0,
    [switch]$Update
)
$ErrorActionPreference = 'Stop'

function Convert-Quantity([string]$text, [hashtable]$units) {
    $clean = $text.Trim().Trim('"')
    if ($clean -eq '' -or $clean -eq '-' -or $clean -eq 'NA') { return $null }
    if ($clean -notmatch '^([0-9.,]+)\s*(\S+)$') { throw "cannot read '$text'" }
    $number = [double]::Parse($Matches[1].Replace(',', ''), [Globalization.CultureInfo]::InvariantCulture)
    $unit = $Matches[2].Replace([char]0x00B5, [char]0x03BC)
    if (-not $units.ContainsKey($unit)) { throw "unknown unit '$unit' in '$text'" }
    return $number * $units[$unit]
}

# The micro sign is written as a char code: Windows PowerShell 5.1 reads this file as ANSI.
# The micro sign (U+00B5) and the Greek mu (U+03BC) are one key for a case-insensitive hashtable: normalise to mu.
$time = @{ 'ns' = 1.0; ([string][char]0x03BC + 's') = 1e3; 'us' = 1e3; 'ms' = 1e6; 's' = 1e9 }
$size = @{ 'B' = 1.0; 'KB' = 1024.0; 'MB' = 1048576.0; 'GB' = 1073741824.0 }
$measured = [ordered]@{}
foreach ($csv in Get-ChildItem $Results -Filter '*-report.csv') {
    foreach ($row in (Get-Content $csv.FullName -Encoding utf8 | ConvertFrom-Csv -Delimiter ';')) {
        $key = $row.Method + $(if ($row.PSObject.Properties.Name -contains 'Objects') { '|' + $row.Objects } else { '' })
        $measured[$key] = [ordered]@{ mean_ns = Convert-Quantity $row.Mean $time; allocated_bytes = Convert-Quantity $row.Allocated $size }
    }
}
if ($measured.Count -eq 0) { throw "no benchmark reports in $Results" }

if ($Update) {
    [IO.File]::WriteAllText($Baseline, ($measured | ConvertTo-Json -Depth 3) + "`n", [Text.UTF8Encoding]::new($false))
    Write-Output "baseline written: $Baseline ($($measured.Count) benchmarks)"
    exit 0
}

$base = Get-Content $Baseline -Raw | ConvertFrom-Json
$failures = @()
foreach ($key in $measured.Keys) {
    $now = $measured[$key]
    $was = $base.$key
    if ($null -eq $was) { Write-Output "new: $key"; continue }
    if ($null -ne $was.allocated_bytes -and $null -ne $now.allocated_bytes -and $now.allocated_bytes -gt $was.allocated_bytes * (1 + $AllocationTolerance) + 1024) {
        $failures += "$key allocates $([math]::Round($now.allocated_bytes / 1MB, 2)) MB, baseline $([math]::Round($was.allocated_bytes / 1MB, 2)) MB"
    }
    if ($now.mean_ns -gt $was.mean_ns * $TimeFactor) {
        $failures += "$key takes $([math]::Round($now.mean_ns / 1e6, 2)) ms, more than ${TimeFactor}x the baseline $([math]::Round($was.mean_ns / 1e6, 2)) ms"
    }
}
if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output "REGRESSION: $_" }
    exit 1
}
Write-Output "bench gate: $($measured.Count) benchmarks within the baseline"
