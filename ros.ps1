# Compatibility alias for praxis.ps1 (DF-ROS-2026-A050); new instructions use praxis.
& (Join-Path $PSScriptRoot "praxis.ps1") @args
exit $LASTEXITCODE
