# Offline orchestration checks. No GitHub/Nexus requests or real pushes are made.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$temporary = Join-Path $root ('artifacts/release-automation/tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary -Force | Out-Null
$previousTestState = Get-Variable MfeReleaseTestState -Scope Global -ValueOnly -ErrorAction SilentlyContinue
$global:MfeReleaseTestState = @{ Release=$null; Latest=$null; Calls=[Collections.Generic.List[string]]::new(); Branch="dev"; PushCount=0 }
$commit = 'a' * 40
$archive = Join-Path $temporary 'MFE.zip'
[IO.File]::WriteAllText($archive,'test archive')
$manifest = @{ version='1.0.0.2'; tag='v1.0.0.2'; channel='Stable'; commit=$commit; dirty=$false; archive='MFE.zip'; archiveSha256=(Get-FileHash $archive).Hash }
$manifestPath = Join-Path $temporary 'release-manifest.json'
function Save-Manifest { $manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath }
Save-Manifest
[IO.File]::WriteAllText((Join-Path $temporary 'release-notes.md'), "Fix texture imports`n`nKeep the exact commit body.`n")
[IO.File]::WriteAllText((Join-Path $temporary 'SHA256SUMS.txt'),'test checksum')

function gh {
    $arguments = @($args)
    $global:MfeReleaseTestState.Calls.Add($arguments -join '|')
    $global:LASTEXITCODE = 0
    if ($arguments[0] -eq 'api') {
        if ($arguments[1] -like '*/commits/*') { return $commit }
        $found = if ($arguments[1].EndsWith('/latest')) { $global:MfeReleaseTestState.Latest } else { $global:MfeReleaseTestState.Release }
        if ($null -eq $found) { $global:LASTEXITCODE = 1; return 'gh: Not Found (HTTP 404)' }
        return ($found | ConvertTo-Json)
    }
    if ($arguments[0] -ne 'release') { throw 'Unexpected mocked command.' }
    switch ($arguments[1]) {
        create {
            $notesIndex = [Array]::IndexOf($arguments,'--notes-file')
            $global:MfeReleaseTestState.Release = @{ body=[IO.File]::ReadAllText($arguments[$notesIndex+1]); draft=$true; prerelease=($arguments -contains '--prerelease') }
        }
        edit {
            $notesIndex = [Array]::IndexOf($arguments,'--notes-file')
            if ($notesIndex -ge 0) { $global:MfeReleaseTestState.Release.body = [IO.File]::ReadAllText($arguments[$notesIndex+1]) }
            if ($arguments -contains '--draft=false') { $global:MfeReleaseTestState.Release.draft = $false }
        }
        delete { $global:MfeReleaseTestState.Release = $null }
        default { throw 'Unexpected mocked release command.' }
    }
}
function Run-Stage([string]$Stage,[string]$NexusVersionId) {
    & "$PSScriptRoot/Publish-Release.ps1" -Stage $Stage -ReleaseDirectory $temporary -NexusVersionId $NexusVersionId
}
function Assert([bool]$Condition,[string]$Message) { if (!$Condition) { throw $Message } }
function Expect-Failure([scriptblock]$Action,[string]$Message) {
    try { & $Action } catch { if ($_.Exception.Message -like $Message) { return }; throw }
    throw "Expected rejection: $Message"
}

$savedOutput = $env:GITHUB_OUTPUT
$env:GITHUB_OUTPUT = Join-Path $temporary 'outputs.txt'
try {
    Run-Stage Prepare
    Assert $global:MfeReleaseTestState.Release.draft 'A new release must remain a draft.'
    Assert ($global:MfeReleaseTestState.Release.body.StartsWith("Fix texture imports`n`nKeep the exact commit body.")) 'Commit message changed.'
    Expect-Failure { Run-Stage Publish } '*Nexus upload must complete*'
    Run-Stage BeginNexus
    Expect-Failure { Run-Stage Prepare } '*uncertain result*'
    Run-Stage CompleteNexus '12345'
    $callCount = $global:MfeReleaseTestState.Calls.Count
    Run-Stage Prepare
    Assert (($global:MfeReleaseTestState.Calls | Select-Object -Skip $callCount | Where-Object { $_ -like 'release|*' }).Count -eq 0) 'Retry replaced already uploaded assets.'
    Assert ((Get-Content -Raw $env:GITHUB_OUTPUT).Contains('skip_nexus=true')) 'Retry did not skip Nexus.'
    Run-Stage Publish
    Assert (!$global:MfeReleaseTestState.Release.draft) 'Completed release was not published.'
    Run-Stage Prepare
    Assert ((Get-Content -Raw $env:GITHUB_OUTPUT).Contains('published=true')) 'Published retry was not recognised.'
    $global:MfeReleaseTestState.Latest = @{tag_name='v1.0.0.3'}
    Expect-Failure { Run-Stage Prepare } '*refusing to downgrade Nexus*'
    $global:MfeReleaseTestState.Latest = $null
    $global:MfeReleaseTestState.Release.body = 'Manually authored release for another commit'
    Expect-Failure { Run-Stage Prepare } '*another source*'
    $manifest.dirty = $true
    Save-Manifest
    Expect-Failure { Run-Stage Prepare } '*validation packages cannot be published*'
    Write-Host 'PASS release stages, exact commit notes, retry recovery, source collision, downgrade and dirty-package checks.'

    $manifest.dirty = $false
    Save-Manifest
    Expect-Failure { Run-Stage PublishBeta } '*does not match the release channel*'
    $manifest.channel = 'Beta'
    $manifest.tag = "v$($manifest.version)-beta.$($commit.Substring(0,12))"
    Save-Manifest
    $global:MfeReleaseTestState.Release = $null
    Expect-Failure { Run-Stage Prepare } '*does not match the release channel*'
    $callCount = $global:MfeReleaseTestState.Calls.Count
    Run-Stage PublishBeta
    Assert $global:MfeReleaseTestState.Release.prerelease 'Beta was not marked as a prerelease.'
    Assert (!$global:MfeReleaseTestState.Release.draft) 'Beta was left as a private draft.'
    $betaCalls = @($global:MfeReleaseTestState.Calls | Select-Object -Skip $callCount)
    Assert (@($betaCalls | Where-Object { $_ -like 'release|create|*' -and $_ -like '*|--latest=false*' }).Count -eq 1) 'Beta creation could become the latest stable release.'
    Assert (@($betaCalls | Where-Object { $_ -like 'release|edit|*' -and $_ -like '*|--latest=false*' }).Count -eq 1) 'Beta publication could become the latest stable release.'
    Assert (@($betaCalls | Where-Object { $_ -like '*mfe-nexus*' -or $_ -like 'api|*/releases/latest' }).Count -eq 0) 'Beta touched stable publication state.'
    $callCount = $global:MfeReleaseTestState.Calls.Count
    Run-Stage PublishBeta
    Assert (@($global:MfeReleaseTestState.Calls | Select-Object -Skip $callCount | Where-Object { $_ -like 'release|*' }).Count -eq 0) 'Published beta was uploaded again.'
    Write-Host 'PASS shareable beta publication, channel separation, stable latest protection and idempotent retries.'

    function git {
        $global:LASTEXITCODE = 0
        switch ($args[0]) {
            'symbolic-ref' { return $global:MfeReleaseTestState.Branch }
            'remote' { return 'https://github.com/AudemusN7/LEBioMorphFaceEditor.git' }
            'push' {
                Assert (($args -join '|') -eq "push|origin|HEAD:refs/heads/$($global:MfeReleaseTestState.Branch)") 'Hook pushed an unexpected ref or used extra options.'
                $global:MfeReleaseTestState.PushCount++
            }
            default { throw 'Unexpected mocked Git command.' }
        }
    }
    $global:MfeReleaseTestState.PushCount = 0
    $global:MfeReleaseTestState.Branch = 'codex/test'
    & "$PSScriptRoot/Push-ReleaseBranch.ps1"
    Assert ($global:MfeReleaseTestState.PushCount -eq 0) 'An unrelated branch was pushed automatically.'
    $global:MfeReleaseTestState.Branch = 'dev'
    & "$PSScriptRoot/Push-ReleaseBranch.ps1"
    Assert ($global:MfeReleaseTestState.PushCount -eq 1) 'dev was not pushed exactly once.'
    $global:MfeReleaseTestState.Branch = 'main'
    & "$PSScriptRoot/Push-ReleaseBranch.ps1"
    Assert ($global:MfeReleaseTestState.PushCount -eq 2) 'main was not pushed exactly once.'
    Write-Host 'PASS local hooks push dev and main only, with exact branch refs and no force-push.'
} finally {
    $env:GITHUB_OUTPUT = $savedOutput
    if ($null -eq $previousTestState) { Remove-Variable MfeReleaseTestState -Scope Global }
    else { $global:MfeReleaseTestState = $previousTestState }
}
