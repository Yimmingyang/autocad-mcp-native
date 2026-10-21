param(
  [Parameter(Mandatory=$true)][ValidateSet('2020','2021','2022','2023','2024','2025','2026','2027')][string]$CadYear,
  [Parameter(Mandatory=$true)][string]$AutoCADDir,
  [Parameter(Mandatory=$true)][string]$OutputDirectory,
  [ValidateSet('8','10')][string]$DotNetMajor = '8'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$acadExe = Join-Path $AutoCADDir 'acad.exe'
foreach ($name in @('acad.exe','acmgd.dll','acdbmgd.dll','accoremgd.dll')) {
  if (-not (Test-Path (Join-Path $AutoCADDir $name))) { throw "Required AutoCAD file not found: $AutoCADDir\$name" }
}
if ($CadYear -in @('2020','2021','2022','2023','2024') -and $DotNetMajor -ne '8') { throw "AutoCAD $CadYear targets .NET Framework, not .NET $DotNetMajor." }
if ($CadYear -eq '2027' -and $DotNetMajor -ne '10') { throw 'AutoCAD 2027 requires .NET 10.' }
if ($CadYear -in @('2025','2026','2027') -and $DotNetMajor -eq '10') { $tfm = 'net10.0-windows' } elseif ($CadYear -in @('2025','2026')) { $tfm = 'net8.0-windows' } else { $tfm = if ($CadYear -eq '2020') { 'net47' } else { 'net48' } }
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { $dotnet = Get-Command "$env:ProgramFiles\dotnet\dotnet.exe" -ErrorAction SilentlyContinue }
if (-not $dotnet) { throw 'A .NET SDK is required to build against the installed AutoCAD API.' }
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($acadExe).ProductVersion
$series = switch ($CadYear) { '2020' {'R23.1'} '2021' {'R24.0'} '2022' {'R24.1'} '2023' {'R24.2'} '2024' {'R24.3'} '2025' {'R25.0'} '2026' {'R25.1'} '2027' {'R26.0'} }
$target = Join-Path $env:TEMP ("AutoCadMcp-build-{0}-{1}" -f $CadYear,[guid]::NewGuid().ToString('N'))
$buildArgs = @('build',(Join-Path $repo 'plugin\AutoCadMcpPlugin.Legacy.csproj'),'-c','Release')
$buildArgs += "-p:CadYear=$CadYear"
$buildArgs += "-p:AutoCADDir=$AutoCADDir"
$buildArgs += "-p:DotNetMajor=$DotNetMajor"
$buildArgs += @('-o',$target,'--nologo','--verbosity','quiet')
& $dotnet.Source @buildArgs
if ($LASTEXITCODE -ne 0) { throw "AutoCAD $CadYear plugin build failed with exit code $LASTEXITCODE" }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$bundle = Join-Path $OutputDirectory 'AutoCadMcp.bundle'
if (Test-Path $bundle) { throw "Output bundle already exists; choose an empty output directory to preserve existing files: $bundle" }
$payload = Join-Path $bundle 'Contents\Windows'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
Get-ChildItem $target -File | Where-Object { $_.Extension -notin @('.pdb') -and $_.Name -notlike '*.runtimeconfig.json' } | Copy-Item -Destination $payload
$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<ApplicationPackage SchemaVersion="1.0" AutodeskProduct="AutoCAD" Name="AutoCAD MCP $CadYear" AppVersion="0.6.2" ProductCode="{$([guid]::NewGuid().ToString())}" UpgradeCode="{7B20D7B7-18F7-4B77-A17D-AED95E33C105}">
  <CompanyDetails Name="AutoCAD MCP Native" />
  <Components>
    <RuntimeRequirements OS="Win64" Platform="AutoCAD*" SeriesMin="$series" SeriesMax="$series" />
    <ComponentEntry AppName="AutoCadMcpPluginV2" ModuleName="./Contents/Windows/AutoCadMcpPluginV2.dll" AppType=".Net" LoadOnAutoCADStartup="True" />
  </Components>
</ApplicationPackage>
"@
$manifest | Set-Content -Path (Join-Path $bundle 'PackageContents.xml') -Encoding utf8
[xml]$check = Get-Content (Join-Path $bundle 'PackageContents.xml')
if ($check.ApplicationPackage.Components.RuntimeRequirements.SeriesMin -ne $series) { throw 'Generated bundle manifest failed its series validation.' }
if (-not (Test-Path (Join-Path $payload 'AutoCadMcpPluginV2.dll'))) { throw 'Generated bundle does not contain the plugin DLL.' }
[pscustomobject]@{ Year=$CadYear; Series=$series; ProductVersion=$version; Framework=$tfm; Bundle=$bundle; BuildOutput=$target }
