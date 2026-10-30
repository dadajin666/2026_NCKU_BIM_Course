param([ValidateSet('2027','2024')][string]$RevitVersion = '2027')
$ErrorActionPreference = 'Stop'
$framework = if ($RevitVersion -eq '2027') { 'net10.0-windows' } else { 'net48' }
$source = Join-Path $PSScriptRoot "bin\$RevitVersion\Release\$framework\Group3.EgressExporter.dll"
if (!(Test-Path -LiteralPath $source)) { throw "Build first: $source" }
$root = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
$packageRoot = Join-Path $root 'Group3.EgressExporter'
# Keep loaded assemblies intact. Revit reads the updated manifest on its next start.
$hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
$target = Join-Path $packageRoot $hash
[IO.Directory]::CreateDirectory($target) | Out-Null
$assembly = Join-Path $target 'Group3.EgressExporter.dll'
if (!(Test-Path -LiteralPath $assembly)) {
    Copy-Item -LiteralPath $source -Destination $assembly
}
if ((Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) {
    throw 'Installed assembly hash mismatch; manifest was not changed.'
}
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
$manifestPath = Join-Path $root 'Group3.EgressExporter.addin'
$pendingManifest = Join-Path $root ('Group3.EgressExporter.' + [Guid]::NewGuid().ToString('N') + '.tmp')
[IO.File]::WriteAllText($pendingManifest, $manifest, [Text.UTF8Encoding]::new($false))
if (Test-Path -LiteralPath $manifestPath) {
    $backup = Join-Path $packageRoot ('manifest-' + [Guid]::NewGuid().ToString('N') + '.backup')
    [IO.File]::Replace($pendingManifest, $manifestPath, $backup)
} else {
    [IO.File]::Move($pendingManifest, $manifestPath)
}
Write-Output "Prepared for next Revit start: $assembly"
Write-Output 'Save your model and restart Revit to load the add-in.'
