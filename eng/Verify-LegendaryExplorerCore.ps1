[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$dependencyDirectory = Join-Path $PSScriptRoot '..\lib\LegendaryExplorerCore'
$expected = [ordered]@{
    'CompressionWrappers.dll' = 'D0417B39CE27BBA575EEC6FB529DFB0614F14750636F0242C0AA7B00CBA7B745'
    'ISACTTools.dll' = '0E96E67D4D09C7EAABE48FDD3D8B42E659956A9F50B42A759219EE22AE204048'
    'LegendaryExplorerCore.dll' = '5BEFC63667C5B14E1605E8DBB6183ED9C784FCBC9F15B0F2F1B64E97ED6089B0'
    'LegendaryExplorerCore.pdb' = '67A202181CFD9EA01DE317D932BF0601DEE58BA141D184B8BD341E7DF4F3B0F6'
    'TexConverter.dll' = 'CE8D9728246585F0389F50699B3BA37F3D3FECE722E4770B346583CAECBC5607'
}

$failed = $false
foreach ($entry in $expected.GetEnumerator()) {
    $path = Join-Path $dependencyDirectory $entry.Key
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Write-Error "Missing pinned dependency: $path" -ErrorAction Continue
        $failed = $true
        continue
    }

    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($actual -ne $entry.Value) {
        Write-Error "Hash mismatch for $($entry.Key): expected $($entry.Value), got $actual" -ErrorAction Continue
        $failed = $true
    } else {
        Write-Host "OK $($entry.Key) $actual"
    }
}

if ($failed) {
    exit 1
}
