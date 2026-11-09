param(
    [Parameter(Mandatory)][ValidateSet('Prepare','BeginNexus','CompleteNexus','Publish','PublishBeta')][string]$Stage,
    [Parameter(Mandatory)][string]$ReleaseDirectory,
    [string]$NexusVersionId
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repository = 'AudemusN7/LEBioMorphFaceEditor'
$manifest = Get-Content -Raw -LiteralPath (Join-Path $ReleaseDirectory 'release-manifest.json') | ConvertFrom-Json
if ($manifest.dirty) { throw 'Local validation packages cannot be published.' }
if ($manifest.version -notmatch '^\d+\.\d+\.\d+\.\d+$' -or $manifest.commit -notmatch '^[0-9a-f]{40}$' -or
    $manifest.channel -notin @('Stable','Beta')) { throw 'Invalid release metadata.' }
$expectedTag = if ($manifest.channel -eq 'Beta') { "v$($manifest.version)-beta.$($manifest.commit.Substring(0,12))" } else { "v$($manifest.version)" }
if ($manifest.tag -ne $expectedTag) { throw 'Release tag does not match its version, source and channel.' }
if (($Stage -eq 'PublishBeta') -ne ($manifest.channel -eq 'Beta')) { throw 'Publication stage does not match the release channel.' }
$tag = $manifest.tag
$sourceMarker = "<!-- mfe-source:$($manifest.commit) -->"
$startedMarker = '<!-- mfe-nexus:started -->'
$donePattern = '<!-- mfe-nexus:complete:\d+ -->'

function Invoke-Gh([string[]]$Arguments) {
    $result = & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "GitHub command failed: $($Arguments[0]) $($Arguments[1])" }
    return ($result -join "`n")
}
function Get-Release([string]$Selector) {
    $result = & gh api "repos/$repository/releases/$Selector" 2>&1
    if ($LASTEXITCODE -eq 0) { return (($result -join "`n") | ConvertFrom-Json) }
    if (($result -join "`n") -match 'HTTP 404') { return $null }
    throw "Unable to read GitHub release $Selector."
}
function Set-Notes([string]$Body) {
    $path = Join-Path $ReleaseDirectory 'publication-notes.md'
    [IO.File]::WriteAllText($path, $Body)
    $null = Invoke-Gh @('release','edit',$tag,'--repo',$repository,'--notes-file',$path)
}
function Set-Output([string]$Name,[string]$Value) {
    if ($env:GITHUB_OUTPUT) { "$Name=$Value" | Add-Content -LiteralPath $env:GITHUB_OUTPUT }
    Write-Host "$Name=$Value"
}
function Assert-TagCommit {
    $tagCommit = & gh api "repos/$repository/commits/$tag" --jq .sha 2>&1
    if ($LASTEXITCODE -eq 0) {
        if (($tagCommit -join '').Trim() -ne $manifest.commit) { throw "Tag $tag points at another commit." }
    } elseif (($tagCommit -join "`n") -notmatch 'HTTP (404|422)') { throw 'Unable to verify release tag.' }
}

$release = Get-Release "tags/$tag"
if ($release -and !$release.body.Contains($sourceMarker)) { throw "Release $tag already belongs to another source or was created manually." }
switch ($Stage) {
    PublishBeta {
        Assert-TagCommit
        if ($release -and !$release.prerelease) { throw 'Refusing to replace a stable release with a beta.' }
        if ($release -and !$release.draft) {
            Write-Host "Beta $tag is already published."
            break
        }
        $archive = Join-Path $ReleaseDirectory $manifest.archive
        if ((Get-FileHash -LiteralPath $archive).Hash -ne $manifest.archiveSha256) { throw 'Release archive checksum mismatch.' }
        if ($release) { $null = Invoke-Gh @('release','delete',$tag,'--repo',$repository,'--yes') }
        $notesPath = Join-Path $ReleaseDirectory 'publication-notes.md'
        $body = (Get-Content -Raw -LiteralPath (Join-Path $ReleaseDirectory 'release-notes.md')).TrimEnd() + "`n`n$sourceMarker`n"
        [IO.File]::WriteAllText($notesPath,$body)
        $null = Invoke-Gh @('release','create',$tag,$archive,
            (Join-Path $ReleaseDirectory 'SHA256SUMS.txt'),(Join-Path $ReleaseDirectory 'release-manifest.json'),
            '--repo',$repository,'--target',$manifest.commit,'--title',"Morph Face Editor $($manifest.version) Beta ($($manifest.commit.Substring(0,12)))",
            '--notes-file',$notesPath,'--draft','--prerelease','--latest=false')
        $null = Invoke-Gh @('release','edit',$tag,'--repo',$repository,'--draft=false','--prerelease','--latest=false')
        Write-Host "Published GitHub beta: https://github.com/$repository/releases/tag/$tag"
    }
    Prepare {
        $latest = Get-Release 'latest'
        if ($latest) {
            $latestVersion = [version]$latest.tag_name.TrimStart('v','V')
            if ([version]$manifest.version -lt $latestVersion) { throw 'A newer public version already exists; refusing to downgrade Nexus.' }
        }
        if ($release -and $release.body -match $donePattern) {
            # Nexus already has this exact release. Keep the original assets, even if a retry rebuilt the ZIP.
            Set-Output 'skip_nexus' 'true'
            Set-Output 'published' (!$release.draft).ToString().ToLowerInvariant()
            break
        }
        if ($release -and !$release.draft) { throw 'Public release has no confirmed Nexus upload; inspect it before retrying.' }
        if ($release -and $release.body.Contains($startedMarker)) {
            throw 'A prior Nexus upload has an uncertain result. Inspect Nexus before retrying; see eng/RELEASING.md.'
        }
        $archive = Join-Path $ReleaseDirectory $manifest.archive
        if ((Get-FileHash -LiteralPath $archive).Hash -ne $manifest.archiveSha256) { throw 'Release archive checksum mismatch.' }
        # Refuse to move an existing tag to different code, including on a partially failed run.
        Assert-TagCommit
        if ($release) { $null = Invoke-Gh @('release','delete',$tag,'--repo',$repository,'--yes') }
        $body = (Get-Content -Raw -LiteralPath (Join-Path $ReleaseDirectory 'release-notes.md')).TrimEnd() + "`n`n$sourceMarker`n"
        $notesPath = Join-Path $ReleaseDirectory 'publication-notes.md'
        [IO.File]::WriteAllText($notesPath,$body)
        $null = Invoke-Gh @('release','create',$tag,$archive,
            (Join-Path $ReleaseDirectory 'SHA256SUMS.txt'),(Join-Path $ReleaseDirectory 'release-manifest.json'),
            '--repo',$repository,'--target',$manifest.commit,'--title',"Morph Face Editor $($manifest.version)",
            '--notes-file',$notesPath,'--draft')
        Set-Output 'skip_nexus' 'false'
        Set-Output 'published' 'false'
    }
    BeginNexus {
        if (!$release -or !$release.draft -or $release.body.Contains($startedMarker)) { throw 'Nexus upload is not in a fresh draft state.' }
        Set-Notes ($release.body.TrimEnd() + "`n$startedMarker`n")
    }
    CompleteNexus {
        if ($NexusVersionId -notmatch '^\d+$') { throw 'Nexus did not return a valid file version ID.' }
        if (!$release -or !$release.draft -or !$release.body.Contains($startedMarker)) { throw 'Missing Nexus upload attempt marker.' }
        Set-Notes ($release.body.TrimEnd() + "`n<!-- mfe-nexus:complete:$NexusVersionId -->`n")
    }
    Publish {
        if (!$release -or $release.body -notmatch $donePattern) { throw 'Nexus upload must complete before publishing GitHub.' }
        if ($release.draft) { $null = Invoke-Gh @('release','edit',$tag,'--repo',$repository,'--draft=false','--latest') }
        Write-Host "Published $tag on GitHub and Nexus."
    }
}
