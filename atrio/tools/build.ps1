# Builds Atrio and leaves the portable folder ready to use. It installs nothing in Windows: if there is no .NET SDK, it
# downloads one to .tools\ inside the repository.
#
#   powershell -ExecutionPolicy Bypass -File atrio\tools\build.ps1
#
#   -Output <folder>   Where to leave it. By default, output\Atrio. It keeps whatever data is in there.
#   -Bench             Adds the off-screen test bench (only for development).
#   -Zip               Also makes the packages to publish: output\Atrio-<version>.zip, the one for updates, and
#                      output\Atrio.zip, the one to download, with everything inside an Atrio folder.
#   -Version <x.y.z>   The version the program will say. Without it, the one in the source code, which is lower than any
#                      published version.
param(
    [string]$Output = '',
    [switch]$Bench,
    [switch]$Zip,
    [string]$Version = ''
)
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$tools = Join-Path $root '.tools'
if ($Output -eq '') { $Output = Join-Path $root 'output\Atrio' }
New-Item -ItemType Directory -Force $Output | Out-Null
$Output = (Resolve-Path $Output).Path
$sources = Join-Path $root 'source'
$atrioInfo = Join-Path $sources 'Playnite\Atrio\AtrioInfo.cs'
$versionPattern = 'const string Version = "([^"]+)"'
# -Version x.y.z or x.y.z-beta (the Release's tag): the program carries only the number, which is what it compares, and
# the zip for updates, the whole tag. (alfa: how it was spelled before)
if ($Version -ne '' -and $Version -notmatch '^\d+\.\d+\.\d+(-(alpha|alfa|beta))?$') { throw "Not a valid version: $Version" }
$versionNumber = $Version -replace '-.*$', ''

function Find-Dotnet {
    $local = Join-Path $tools 'dotnet\dotnet.exe'
    if (Test-Path $local) { return $local }
    $global = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($global) {
        $sdks = & $global.Source --list-sdks 2>$null
        if ($sdks) { return $global.Source }
    }
    Write-Host 'There is no .NET SDK: downloading one to .tools\dotnet'
    New-Item -ItemType Directory -Force $tools | Out-Null
    $installer = Join-Path $tools 'dotnet-install.ps1'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
    & $installer -Channel 9.0 -InstallDir (Join-Path $tools 'dotnet') -NoPath *>&1 | Out-Null
    return $local
}

$dotnet = Find-Dotnet
$python = (Get-Command python -ErrorAction Stop).Source
Write-Output "dotnet: $dotnet"

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$env:MSBuildEnableWorkloadResolver = 'false'
if (-not $env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $tools 'nuget' }

$projects = 'Playnite.DesktopApp\Playnite.DesktopApp.csproj',
            'Playnite.FullscreenApp\Playnite.FullscreenApp.csproj',
            'Playnite.BrowserProcess\Playnite.BrowserProcess.csproj',
            'Atrio.Updater\Atrio.Updater.csproj'
$temporary = Join-Path $root 'output\build\'
if (Test-Path $temporary) { Remove-Item $temporary -Recurse -Force }
New-Item -ItemType Directory -Force $temporary | Out-Null

# The version only changes while it builds: the source code stays as it was.
$originalInfo = [IO.File]::ReadAllText($atrioInfo)
if ($Version -ne '') {
    [IO.File]::WriteAllText($atrioInfo, ($originalInfo -replace $versionPattern, "const string Version = `"$versionNumber`""))
}

try {
foreach ($project in $projects) {
    $options = @(
        'build', (Join-Path $sources $project), '-nologo', '-c', 'Release', '-v:q',
        '-p:Platform=x86', "-p:OutputPath=$temporary", '-p:AllowedReferenceRelatedFileExtensions=none',
        "-p:SolutionDir=$sources\", '-p:DebugType=none', '-p:DebugSymbols=false'
    )
    if ($Bench) { $options += '-p:AtrioBanco=true' }
    & $dotnet @options
    if ($LASTEXITCODE -ne 0) { throw "Failed to build $project" }
}

# Atrio doesn't load the bridge to OptiScaler Client: that program's .NET does, so it is for another platform and goes in
# its own folder.
& $dotnet 'build' (Join-Path $sources 'Atrio.OptiScaler\Atrio.OptiScaler.csproj') '-nologo' '-c' 'Release' '-v:q' `
    "-p:OutputPath=$($temporary)Resources\OptiScaler\" '-p:AppendTargetFrameworkToOutputPath=false' "-p:SolutionDir=$sources\" `
    '-p:DebugType=none' '-p:DebugSymbols=false'
if ($LASTEXITCODE -ne 0) { throw 'Failed to build the bridge to OptiScaler Client' }
} finally {
    [IO.File]::WriteAllText($atrioInfo, $originalInfo)
}

Push-Location $temporary
# The same that Playnite's official build does with these files.
Remove-Item 'System.Management.Automation.dll', 'Windows.winmd', 'CefSharp.BrowserSubprocess.exe', 'crash_reporter.cfg' -Force -ErrorAction SilentlyContinue
Get-ChildItem -Filter '*.pdb' | Remove-Item -Force

# Only the file's name changes. The assembly's identity is still Playnite's, which is what add-ons and themes refer to.
$names = @{ 'Playnite.DesktopApp' = 'Atrio'; 'Playnite.FullscreenApp' = 'Atrio.Fullscreen'; 'Playnite.BrowserProcess' = 'Atrio.Browser' }
foreach ($old in $names.Keys) {
    foreach ($extension in 'exe', 'exe.config') {
        if (Test-Path "$old.$extension") { Move-Item "$old.$extension" "$($names[$old]).$extension" -Force }
    }
}
& $python (Join-Path $root 'atrio\tools\fix_config.py') 'Atrio.exe.config' 'Atrio.Fullscreen.exe.config'
Pop-Location

& $python (Join-Path $root 'atrio\tools\themes.py') (Join-Path $temporary 'Themes\Desktop') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Failed to generate the themes' }
& $python (Join-Path $root 'atrio\tools\addons.py') 'install' $temporary | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Failed to prepare the add-ons' }

Copy-Item (Join-Path $root 'atrio\README.txt') (Join-Path $temporary 'README.txt') -Force
Copy-Item (Join-Path $root 'atrio\LEEME.txt') (Join-Path $temporary 'LEEME.txt') -Force
Copy-Item (Join-Path $root 'LICENSE.md') (Join-Path $temporary 'LICENSE.md') -Force
# The notices of other people's libraries that travel in the package and ask for their license to go with them.
Copy-Item (Join-Path $root 'atrio\licenses') (Join-Path $temporary 'Licenses') -Recurse -Force

# What belongs to this version: on updating, whatever is no longer here is removed. Nothing else is touched.
# (The list keeps its Spanish name: it is the one every installed version has.)
$list = Join-Path $temporary 'atrio-archivos.txt'
$start = (Resolve-Path $temporary).Path.TrimEnd('\').Length + 1
Get-ChildItem $temporary -Recurse -File | ForEach-Object { $_.FullName.Substring($start) } | Set-Content $list -Encoding UTF8
Add-Content $list 'atrio-archivos.txt' -Encoding UTF8

# It copies only the program: library, ExtensionsData and the user's configuration are not here.
Copy-Item (Join-Path $temporary '*') $Output -Recurse -Force
Write-Output "ready: $Output"

if ($Zip) {
    $versionName = if ($Version -ne '') { $Version } else { [regex]::Match($originalInfo, $versionPattern).Groups[1].Value }
    # The one for updates, with the files at its root: it is the one every installed version looks for.
    $archive = Join-Path $root "output\Atrio-$versionName.zip"
    if (Test-Path $archive) { Remove-Item $archive -Force }
    Compress-Archive -Path (Join-Path $temporary '*') -DestinationPath $archive
    Write-Output "package: $archive"

    # The one to download by hand: the same inside an Atrio folder, so that unpacking it doesn't scatter it.
    # Its name doesn't start with "Atrio-", so the versions already installed don't take it for the other one.
    $wrapper = Join-Path $root 'output\wrapper'
    if (Test-Path $wrapper) { Remove-Item $wrapper -Recurse -Force }
    New-Item -ItemType Directory -Force $wrapper | Out-Null
    Move-Item $temporary (Join-Path $wrapper 'Atrio')
    $download = Join-Path $root 'output\Atrio.zip'
    if (Test-Path $download) { Remove-Item $download -Force }
    Compress-Archive -Path (Join-Path $wrapper 'Atrio') -DestinationPath $download
    Write-Output "download: $download"
    $temporary = $wrapper
}
Remove-Item $temporary -Recurse -Force
