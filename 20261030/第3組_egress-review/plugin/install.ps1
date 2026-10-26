param([ValidateSet('2027','2024')][string]$RevitVersion = '2027')
$ErrorActionPreference = 'Stop'
$framework = if ($RevitVersion -eq '2027') { 'net10.0-windows' } else { 'net48' }
$source = Join-Path $PSScriptRoot "bin\$RevitVersion\Release\$framework\Group3.EgressExporter.dll"
if (!(Test-Path -LiteralPath $source)) { throw "Build first: $source" }
$root = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
$target = Join-Path $root 'Group3.EgressExporter'
[IO.Directory]::CreateDirectory($target) | Out-Null
$assembly = Join-Path $target 'Group3.EgressExporter.dll'
# A loaded DLL cannot be replaced; fail clearly and keep existing manifest intact.
Copy-Item -LiteralPath $source -Destination $assembly -Force
$escapedAssembly = [Security.SecurityElement]::Escape($assembly)
$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>Group3 Egress Exporter</Name>
    <Assembly>$escapedAssembly</Assembly>
    <AddInId>627C658A-AC26-456A-B0F5-2E3EA2FC7664</AddInId>
    <FullClassName>Group3.EgressExporter.App</FullClassName>
    <VendorId>G3EG</VendorId>
    <VendorDescription>Group 3 read-only egress data exporter</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
[IO.File]::WriteAllText((Join-Path $root 'Group3.EgressExporter.addin'), $manifest, [Text.UTF8Encoding]::new($false))
Write-Output "Installed: $assembly"
Write-Output 'Save your model and restart Revit to load the add-in.'
