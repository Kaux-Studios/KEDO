# Runs the addon's headless GDScript tests with a Godot 4.6 binary.
# Usage: tools/godot-test.ps1 -GodotPath <path to godot(.exe)> [-Content samples/outpost] [-Schema samples/outpost/schema]
param(
    [Parameter(Mandatory = $true)][string]$GodotPath,
    [string]$Content = "samples/outpost",
    [string]$Schema = "samples/outpost/schema"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "tests\godot"
$addonSrc = Join-Path $root "addons\kedo"
$addonDst = Join-Path $project "addons\kedo"
if (Test-Path $addonDst) { Remove-Item -Recurse -Force $addonDst }
New-Item -ItemType Directory -Force $addonDst | Out-Null
Copy-Item -Recurse -Force (Join-Path $addonSrc "*") $addonDst

$cli = Join-Path $root "src\Kedo.Cli\bin\Release\net8.0\kedo.exe"
if (-not (Test-Path $cli)) { throw "build the CLI first: dotnet build src/Kedo.Cli -c Release" }
$contentPath = Join-Path $root $Content
$schemaPath = Join-Path $root $Schema

& $GodotPath --headless --path $project --import | Out-Null
& $GodotPath --headless --path $project -s res://test_runner.gd -- $cli $contentPath $schemaPath
exit $LASTEXITCODE
