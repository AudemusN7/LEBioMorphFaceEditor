param([ValidateSet('Fix','Improvement','Feature','Major')][string]$Kind = 'Fix')
$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot '../../Version.props'
$text = [IO.File]::ReadAllText($path)
$match = [regex]::Match($text, '<Version>(\d+\.\d+\.\d+\.\d+)</Version>')
if (!$match.Success) { throw 'Version.props has no valid four-part version.' }
$old = [version]$match.Groups[1].Value
$next = switch ($Kind) {
    Fix { "$($old.Major).$($old.Minor).$($old.Build).$($old.Revision + 1)" }
    Improvement { "$($old.Major).$($old.Minor).$($old.Build + 1).0" }
    Feature { "$($old.Major).$($old.Minor + 1).0.0" }
    Major { "$($old.Major + 1).0.0.0" }
}
$null = [version]$next
[IO.File]::WriteAllText($path,$text.Replace($match.Value,"<Version>$next</Version>"))
Write-Output "$old -> $next"
