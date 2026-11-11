param(
    [string]$OutputDirectory = 'artifacts/releases',
    [ValidateSet('Stable','Beta')][string]$Channel = 'Stable',
    [switch]$AllowDirty
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $repo
try {
    $dirty = !!(git status --porcelain --untracked-files=normal)
    if ($dirty -and !$AllowDirty) { throw 'Release builds require a clean checkout. Use -AllowDirty only for local packaging validation.' }
    $commit = (git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Unable to read the source commit.' }
    [xml]$props = Get-Content -Raw Version.props
    $version = [string]$props.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'Version.props must contain a four-part version.' }
    $releaseVersion = if ($Channel -eq 'Beta') { "$version-beta.$($commit.Substring(0,12))" } else { $version }
    $tag = "v$releaseVersion"
    $outputRoot = [IO.Path]::GetFullPath($OutputDirectory, $repo)
    $release = Join-Path $outputRoot $tag
    if (Test-Path -LiteralPath $release) { throw "Output already exists: $release. Choose a fresh output directory." }
    $publish = Join-Path $release 'MorphFaceEditor'
    New-Item -ItemType Directory -Path $release -Force | Out-Null

    & "$PSScriptRoot/Verify-LegendaryExplorerCore.ps1"
    if ($LASTEXITCODE -ne 0) { throw 'Pinned dependency verification failed.' }
    & "$PSScriptRoot/Assert-NativeRelease.ps1" lib/LegendaryExplorerCore/TexConverter.dll
    & "$PSScriptRoot/Assert-NativeRelease.ps1" lib/LegendaryExplorerCore/CompressionWrappers.dll
    # PlatformTarget is x64 in Directory.Build.props. Keep restore aligned with the committed RID-free lock files.
    dotnet publish src/MorphFaceEditor/MorphFaceEditor.csproj -c Release --self-contained false -p:RestoreLockedMode=true --disable-build-servers -m:1 -o $publish "-p:SourceRevisionId=$commit"
    if ($LASTEXITCODE -ne 0) { throw 'Release publish failed.' }

    foreach ($file in Get-ChildItem -LiteralPath $publish -Filter 'MorphFaceEditor*.dll' -File) {
        $metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo($file.FullName)
        if ($metadata.FileVersion -ne $version -or $metadata.ProductVersion -ne "$version+$commit") {
            throw "Incorrect version/source metadata in $($file.Name)."
        }
    }
    # This assembly ships reference metadata. Check inside it before archiving:
    # an embedded private fixture has no loose file for the extension check to see.
    $allowedResources = @('MorphFaceEditor.CrossGameAssetReconciliation.json',
        'MorphFaceEditor.MaterialDependencyOracle.json', 'MorphFaceEditor.StockMaterialMetadata.json')
    $library = Join-Path $publish 'MorphFaceEditor.LegendaryExplorer.dll'
    $resources = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($library)).GetManifestResourceNames()
    $unexpectedResources = @($resources | Where-Object { $_ -cnotin $allowedResources })
    if ($unexpectedResources.Count) { throw "Unexpected LegendaryExplorer resource: $($unexpectedResources -join ', ')" }
    foreach ($name in @('LegendaryExplorerCore.dll','CompressionWrappers.dll','TexConverter.dll')) {
        if ((Get-FileHash -LiteralPath (Join-Path $publish $name)).Hash -ne
            (Get-FileHash -LiteralPath (Join-Path $repo "lib/LegendaryExplorerCore/$name")).Hash) {
            throw "Published $name does not match the pinned dependency."
        }
    }
    & "$PSScriptRoot/Assert-NativeRelease.ps1" (Join-Path $publish 'MorphFaceEditor.exe')
    $runtime = Get-Content -Raw (Join-Path $publish 'MorphFaceEditor.runtimeconfig.json') | ConvertFrom-Json
    if ($runtime.runtimeOptions.frameworks.name -notcontains 'Microsoft.WindowsDesktop.App') { throw 'Missing Desktop Runtime requirement.' }
    Copy-Item -LiteralPath README.md -Destination $publish
    Copy-Item -LiteralPath documentation -Destination $publish -Recurse
    $files = @(Get-ChildItem -LiteralPath $publish -Recurse -File)
    $unexpected = @($files | Where-Object {
        $_.Extension -in @('.pdb','.pcc','.ron','.br','.7z','.zip','.ps1') -or
        $_.Name -in @('ISACTTools.dll','System.IO.Hashing.dll','sixlabors.lic') -or
        $_.Name -match '^MorphFaceEditor\.(Tests|QA|Localiser|DataCompiler|ShaderCompiler)\.'
    })
    if ($unexpected.Count) { throw "Unexpected release files: $($unexpected.Name -join ', ')" }
    $archiveName = "MorphFaceEditor-v$releaseVersion-win-x64.zip"
    $archivePath = Join-Path $release $archiveName
    [IO.Compression.ZipFile]::CreateFromDirectory($publish, $archivePath)
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        if ($archive.Entries.Count -ne $files.Count) { throw 'Archive file count mismatch.' }
        foreach ($file in $files) {
            $relative = [IO.Path]::GetRelativePath($publish, $file.FullName).Replace('\','/')
            $entry = $archive.GetEntry($relative)
            if ($null -eq $entry -or $entry.Length -ne $file.Length) { throw "Archive entry mismatch: $relative" }
            $stream = $entry.Open()
            try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
            finally { $stream.Dispose() }
            if ($hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw "Archive checksum mismatch: $relative" }
        }
    } finally { $archive.Dispose() }
    $hash = (Get-FileHash -LiteralPath $archivePath).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $release 'SHA256SUMS.txt'), "$hash  $archiveName`n")
    $notes = (git log -1 --format=%B) -join "`n"
    [IO.File]::WriteAllText((Join-Path $release 'release-notes.md'), $notes.TrimEnd() + "`n")
    [ordered]@{
        version=$version; tag=$tag; channel=$Channel; commit=$commit; dirty=$dirty
        archive=$archiveName; archiveSha256=$hash; fileCount=$files.Count
        requiredRuntime='.NET 10 Desktop Runtime (x64)'; selfContained=$false
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $release 'release-manifest.json') -Encoding utf8NoBOM
    Write-Host "Verified release package: $archivePath"
    if ($env:GITHUB_OUTPUT) {
        "directory=$release`narchive=$archivePath`nversion=$version`ntag=$tag" | Add-Content -LiteralPath $env:GITHUB_OUTPUT
        $delimiter = [guid]::NewGuid().ToString('N')
        "notes<<$delimiter`n$($notes.TrimEnd())`n$delimiter" | Add-Content -LiteralPath $env:GITHUB_OUTPUT
    }
} finally { Pop-Location }
