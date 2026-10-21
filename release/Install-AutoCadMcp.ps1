$ErrorActionPreference = 'Stop'
$bundleName = 'AutoCadMcp.bundle'
$source = Join-Path $PSScriptRoot 'AutoCadMcp.bundle'
$targetRoot = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins'
$target = Join-Path $targetRoot $bundleName
$runningAcad = Get-Process -Name acad -ErrorAction SilentlyContinue
if ($runningAcad) {
  throw 'AutoCAD is running. Save/close AutoCAD first; installer left the current bundle untouched.'
}
if (!(Test-Path (Join-Path $source 'PackageContents.xml'))) { throw "Bundle manifest missing under $source" }
foreach ($suffix in '.dll', '.deps.json', '.runtimeconfig.json') {
  if (!(Test-Path (Join-Path $source "Contents\Windows\AutoCadMcpPluginV2$suffix"))) { throw "Bundle V2 runtime file missing: $suffix" }
}
New-Item -ItemType Directory -Path $targetRoot -Force | Out-Null
$stage = Join-Path $targetRoot ("$bundleName.staging-" + [guid]::NewGuid().ToString('N'))
Copy-Item -LiteralPath $source -Destination $stage -Recurse
[xml]$manifest = Get-Content (Join-Path $stage 'PackageContents.xml')
if ($manifest.ApplicationPackage.Components.ComponentEntry.LoadOnAutoCADStartup -ne 'True') { throw 'Auto-load flag is not enabled' }
foreach ($suffix in '.dll', '.deps.json', '.runtimeconfig.json') {
  if (!(Test-Path (Join-Path $stage "Contents\Windows\AutoCadMcpPluginV2$suffix"))) { throw "Staged V2 runtime file missing: $suffix" }
}
$backup = $null
if (Test-Path -LiteralPath $target) {
  $backup = "$target.backup-$(Get-Date -Format 'yyyyMMdd-HHmmss-fff')"
  Move-Item -LiteralPath $target -Destination $backup
}
try {
  Move-Item -LiteralPath $stage -Destination $target
} catch {
  if ($backup -and !(Test-Path -LiteralPath $target)) { Move-Item -LiteralPath $backup -Destination $target }
  throw
}
Write-Output "Installed: $target"
if ($backup) { Write-Output "Previous bundle backup: $backup" }
Write-Output 'AutoCAD loads this bundle at next startup. Close and reopen AutoCAD once to activate it.'

