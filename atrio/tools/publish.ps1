# Publishes a version in the GitHub Releases, where Atrio finds it by itself.
# It builds from this copy, which has to match main on GitHub. What goes up is what is in the repository.
# First, what changes has to be written in atrio\release-notes\<version>.md and pushed to main.
#
#   powershell -ExecutionPolicy Bypass -File atrio\tools\publish.ps1 -Version 1.2.0-beta
#
#   -Version   The Release's tag: x.y.z-beta (since 1.12.2; 1.12.2 itself went out without the phase so that the earlier
#              copies, which only understand x.y.z, would see it). The program carries only the number.
#   -Draft     Leaves it as a draft: nobody sees it until it is published (gh release edit 1.12.3-beta --draft=false).
#              It is what the build on GitHub does (.github\workflows\publish.yml), to check it first.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [switch]$Draft
)
$ErrorActionPreference = 'Stop'

# Always explicit: this copy also has the project it comes from as a remote.
$repository = 'ElKiro1311/Atrio'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Push-Location $root
try {
    if ($Version -notmatch '^\d+\.\d+\.\d+(-(alpha|alfa|beta))?$') { throw "Not a valid version: $Version" }
    $number = $Version -replace '-.*$', ''
    $notes = "atrio\release-notes\$number.md"
    if (-not (Test-Path $notes)) { throw "$notes is missing" }
    if (git status --porcelain) { throw 'There are uncommitted changes.' }

    git fetch -q origin main
    if ((git rev-parse HEAD) -ne (git rev-parse origin/main)) { throw 'This copy does not match main on GitHub.' }

    # It only goes forward: Atrio ignores any version that is not higher than its own.
    $published = gh release list --repo $repository --limit 100 --json tagName --jq '.[].tagName' |
        Where-Object { $_ -match '^v?\d+\.\d+\.\d+(-(alpha|alfa|beta))?$' } | ForEach-Object { [version]($_.TrimStart('v') -replace '-.*$', '') } | Sort-Object
    if ($LASTEXITCODE -ne 0) { throw 'The list of versions could not be read.' }
    if ($published -and [version]$number -le ($published | Select-Object -Last 1)) {
        throw "The latest published version is already $($published | Select-Object -Last 1)."
    }

    & (Join-Path $PSScriptRoot 'build.ps1') -Zip -Version $Version
    # Two packages with the same: the one for updates, without a folder, which is the one installed versions look for, and
    # the one to download by hand, with everything inside an Atrio folder.
    $package = "output\Atrio-$Version.zip"
    $download = 'output\Atrio.zip'
    foreach ($file in @($package, $download)) {
        if (-not (Test-Path $file)) { throw "$file was not created" }
    }

    # Title: the version with its phase and the subject (the first line of the notes): "1.12.3-beta: ...".
    $phase = [regex]::Match([IO.File]::ReadAllText((Join-Path $root 'source\Playnite\Atrio\AtrioInfo.cs')), 'const string Phase = "([^"]*)"').Groups[1].Value
    $label = if ($Version -match '-') { $Version } elseif ($phase) { "$number-$phase" } else { $number }
    $subject = ((Get-Content $notes -Encoding UTF8 | Where-Object { $_.Trim() } | Select-Object -First 1) -replace '^#+\s*', '').Trim().TrimEnd('.')
    $title = if ($subject) { "${label}: $subject" } else { $label }
    $options = @($Version, $package, $download, '--repo', $repository, '--target', (git rev-parse HEAD), '--title', $title, '--notes-file', $notes)
    if ($Draft) { $options += '--draft' }
    gh release create @options
    if ($LASTEXITCODE -ne 0) { throw 'The version could not be created on GitHub.' }
}
finally {
    Pop-Location
}
