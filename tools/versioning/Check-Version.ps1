param([switch]$Staged)
$ErrorActionPreference = 'Stop'
$text = if ($Staged) { (git show ':Version.props') -join "`n" } else { [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../../Version.props')) }
if ($LASTEXITCODE -and $Staged) { throw 'Stage Version.props before committing.' }
$match = [regex]::Match($text, '<Version>(\d+\.\d+\.\d+\.\d+)</Version>')
if (!$match.Success) { throw 'Version.props must contain four numeric version components.' }
$current = [version]$match.Groups[1].Value
foreach ($part in @($current.Major, $current.Minor, $current.Build, $current.Revision)) {
    if ($part -gt 65534) { throw 'Version components must fit assembly version metadata (0-65534).' }
}
$parents = @('HEAD')
$mergePath = git rev-parse --git-path MERGE_HEAD
if (Test-Path -LiteralPath $mergePath) { $parents += Get-Content -LiteralPath $mergePath }
$highest = $null
foreach ($parent in $parents) {
    $oldText = (git show "${parent}:Version.props" 2>$null) -join "`n"
    $oldMatch = [regex]::Match($oldText, '<Version>(\d+\.\d+\.\d+\.\d+)</Version>')
    if (!$oldMatch.Success) { continue } # Bootstrap commit, before history migration.
    $old = [version]$oldMatch.Groups[1].Value
    if ($current -le $old) { throw "Version $current must exceed parent $parent ($old)." }
    if ($null -eq $highest -or $old -gt $highest) { $highest = $old }
}
if ($null -ne $highest) {
    $oldParts = @($highest.Major, $highest.Minor, $highest.Build, $highest.Revision)
    $newParts = @($current.Major, $current.Minor, $current.Build, $current.Revision)
    for ($index = 0; $index -lt 4; $index++) {
        if ($newParts[$index] -eq $oldParts[$index]) { continue }
        # Intermediate QA builds may consume version numbers before a commit.
        for ($lower = $index + 1; $lower -lt 4; $lower++) {
            if ($newParts[$lower] -ne 0) {
                throw "Version $current must reset components below the increased component of $highest."
            }
        }
        break
    }
}
Write-Output "Version $current is valid."
