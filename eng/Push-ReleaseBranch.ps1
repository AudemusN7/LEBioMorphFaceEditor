$ErrorActionPreference = 'Stop'
$branch = git symbolic-ref --quiet --short HEAD
if ($LASTEXITCODE -ne 0 -or $branch -notin @('main','dev')) { exit 0 }
$remote = git remote get-url --push origin
if ($LASTEXITCODE -ne 0 -or $remote -notmatch '^(https://github\.com/|git@github\.com:)AudemusN7/LEBioMorphFaceEditor(?:\.git)?$') {
    throw 'Automatic release push requires origin to be AudemusN7/LEBioMorphFaceEditor.'
}
Write-Host "Pushing $branch; GitHub Actions will build its release channel."
git push origin "HEAD:refs/heads/$branch"
if ($LASTEXITCODE -ne 0) {
    Write-Warning "The commit is saved locally, but its push failed. Run git push origin $branch to retry."
    exit 1
}
