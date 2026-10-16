# Mirrors .agents/skills (canonical; read by Codex and Cursor) into .claude/skills (read by Claude Code).
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root ".agents\skills"
$dst = Join-Path $root ".claude\skills"
if (Test-Path $dst) { Remove-Item -Recurse -Force $dst }
New-Item -ItemType Directory -Force $dst | Out-Null
Copy-Item -Recurse -Force (Join-Path $src "*") $dst
Write-Host "skills mirrored to .claude/skills"
