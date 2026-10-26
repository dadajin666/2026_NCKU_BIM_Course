param(
    [ValidateSet('2027','2024')][string]$RevitVersion = '2027',
    [string]$Dotnet = 'dotnet',
    [string]$RevitDirectory = "C:\Program Files\Autodesk\Revit $RevitVersion"
)
$ErrorActionPreference = 'Stop'
& $Dotnet build (Join-Path $PSScriptRoot 'EgressExporter.csproj') -c Release "-p:RevitVersion=$RevitVersion" "-p:RevitDirectory=$RevitDirectory"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
