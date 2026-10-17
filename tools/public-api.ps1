# Declares new public API in PublicAPI.Unshipped.txt (RS0016) and drops entries that no longer
# exist (RS0017). `dotnet format` does not apply the analyzer's code fix with SDK 8, so this script
# builds each packable project with a SARIF log and copies the names the analyzer computed.
# Usage: powershell -File tools/public-api.ps1 [-Ship]   (-Ship moves Unshipped into Shipped, for a release)
param([switch]$Ship)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$projects = @('Kedo.Core', 'Kedo.Compiler', 'Kedo.Cli')
$log = Join-Path ([IO.Path]::GetTempPath()) 'kedo-public-api'
New-Item -ItemType Directory -Force $log | Out-Null

function Read-Api([string]$path) {
    @(Get-Content $path -Encoding utf8 | Where-Object { $_ -and -not $_.StartsWith('#nullable') })
}

function Write-Api([string]$path, [string[]]$lines) {
    $sorted = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    foreach ($l in $lines) { if ($l) { [void]$sorted.Add($l) } }
    $text = "#nullable enable`n" + (($sorted | ForEach-Object { "$_`n" }) -join '')
    [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
}

foreach ($p in $projects) {
    $dir = Join-Path $root "src/$p"
    $unshipped = Join-Path $dir 'PublicAPI.Unshipped.txt'
    for ($pass = 0; $pass -lt 3; $pass++) {
        $sarif = Join-Path $log "$p.sarif"
        Remove-Item $sarif -ErrorAction SilentlyContinue
        dotnet build (Join-Path $dir "$p.csproj") -c Release --no-incremental -p:TreatWarningsAsErrors=false "-p:ErrorLog=$sarif" | Out-Null
        $results = (Get-Content $sarif -Raw -Encoding utf8 | ConvertFrom-Json).runs | ForEach-Object { $_.results } |
            Where-Object { $_.ruleId -in 'RS0016', 'RS0017' -and $_.locations[0].resultFile.uri -like "*/src/$p/*" }
        if (-not $results) { break }
        $lines = @(Read-Api $unshipped)
        foreach ($r in $results) {
            if ($r.ruleId -eq 'RS0016') { $lines += $r.properties.customProperties.APIName; continue }
            if ($r.message -notmatch '^[^"'']*["''](.+)["''][^"'']*$') { continue }
            $name = $Matches[1]
            # A removed member that was already shipped stays listed as *REMOVED* until the next release.
            if ($r.locations[0].resultFile.uri -like '*PublicAPI.Shipped.txt') { $lines += "*REMOVED*$name" }
            else { $lines = @($lines | Where-Object { $_ -ne $name }) }
        }
        Write-Api $unshipped $lines
    }
    if ($Ship) {
        $shipped = Join-Path $dir 'PublicAPI.Shipped.txt'
        $new = @(Read-Api $unshipped)
        $removed = @($new | Where-Object { $_.StartsWith('*REMOVED*') } | ForEach-Object { $_.Substring(9) })
        $kept = @(Read-Api $shipped) + @($new | Where-Object { -not $_.StartsWith('*REMOVED*') }) | Where-Object { $_ -notin $removed }
        Write-Api $shipped $kept
        Write-Api $unshipped @()
    }
    Write-Host "$p`: $((Read-Api $unshipped).Count) unshipped, $((Read-Api (Join-Path $dir 'PublicAPI.Shipped.txt')).Count) shipped"
}
