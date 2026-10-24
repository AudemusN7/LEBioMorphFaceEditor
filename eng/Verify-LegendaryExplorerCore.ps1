[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\MorphFaceEditor.LegendaryExplorer\MorphFaceEditor.LegendaryExplorer.csproj'
& dotnet msbuild $project -target:VerifyLegendaryExplorerCorePin -nologo -verbosity:minimal "-property:Configuration=$Configuration"
$verificationExit = $LASTEXITCODE
if ($verificationExit -eq 0) { Write-Output "Pinned LegendaryExplorerCore dependencies verified ($Configuration)." }
exit $verificationExit
