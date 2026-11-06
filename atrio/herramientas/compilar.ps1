# Compila Atrio y deja la carpeta portable lista para usar. No instala nada en Windows:
# si no hay .NET SDK, baja uno a .herramientas\ dentro del repositorio.
#
#   powershell -ExecutionPolicy Bypass -File atrio\herramientas\compilar.ps1
#
#   -Salida <carpeta>  Dónde dejarla. Por defecto salida\Atrio. Conserva los datos que haya en ella.
#   -Banco             Añade el banco de pruebas fuera de pantalla (solo para desarrollo).
#   -Zip               Además crea salida\Atrio-<version>.zip para publicar.
#   -Version <x.y.z>   La versión que dirá el programa. Sin esto, la del código fuente, que es más
#                      baja que cualquier versión publicada.
param(
    [string]$Salida = '',
    [switch]$Banco,
    [switch]$Zip,
    [string]$Version = ''
)
$ErrorActionPreference = 'Stop'

$raiz = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$herramientas = Join-Path $raiz '.herramientas'
if ($Salida -eq '') { $Salida = Join-Path $raiz 'salida\Atrio' }
New-Item -ItemType Directory -Force $Salida | Out-Null
$Salida = (Resolve-Path $Salida).Path
$fuentes = Join-Path $raiz 'source'
$infoAtrio = Join-Path $fuentes 'Playnite\Atrio\AtrioInfo.cs'
$patronVersion = 'const string Version = "([^"]+)"'
if ($Version -ne '' -and $Version -notmatch '^\d+\.\d+\.\d+$') { throw "Versión no válida: $Version" }

function Buscar-Dotnet {
    $local = Join-Path $herramientas 'dotnet\dotnet.exe'
    if (Test-Path $local) { return $local }
    $global = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($global) {
        $sdks = & $global.Source --list-sdks 2>$null
        if ($sdks) { return $global.Source }
    }
    Write-Host 'No hay .NET SDK: bajando uno a .herramientas\dotnet'
    New-Item -ItemType Directory -Force $herramientas | Out-Null
    $instalador = Join-Path $herramientas 'dotnet-install.ps1'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $instalador -UseBasicParsing
    & $instalador -Channel 9.0 -InstallDir (Join-Path $herramientas 'dotnet') -NoPath *>&1 | Out-Null
    return $local
}

$dotnet = Buscar-Dotnet
$python = (Get-Command python -ErrorAction Stop).Source
Write-Output "dotnet: $dotnet"

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$env:MSBuildEnableWorkloadResolver = 'false'
if (-not $env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $herramientas 'nuget' }

$proyectos = 'Playnite.DesktopApp\Playnite.DesktopApp.csproj',
             'Playnite.FullscreenApp\Playnite.FullscreenApp.csproj',
             'Playnite.BrowserProcess\Playnite.BrowserProcess.csproj',
             'Atrio.Updater\Atrio.Updater.csproj'
$temporal = Join-Path $raiz 'salida\compilacion\'
if (Test-Path $temporal) { Remove-Item $temporal -Recurse -Force }
New-Item -ItemType Directory -Force $temporal | Out-Null

# La versión solo cambia mientras se compila: el código fuente se queda como estaba.
$infoOriginal = [IO.File]::ReadAllText($infoAtrio)
if ($Version -ne '') {
    [IO.File]::WriteAllText($infoAtrio, ($infoOriginal -replace $patronVersion, "const string Version = `"$Version`""))
}

try {
foreach ($proyecto in $proyectos) {
    $opciones = @(
        'build', (Join-Path $fuentes $proyecto), '-nologo', '-c', 'Release', '-v:q',
        '-p:Platform=x86', "-p:OutputPath=$temporal", '-p:AllowedReferenceRelatedFileExtensions=none',
        "-p:SolutionDir=$fuentes\", '-p:DebugType=none', '-p:DebugSymbols=false'
    )
    if ($Banco) { $opciones += '-p:AtrioBanco=true' }
    & $dotnet @opciones
    if ($LASTEXITCODE -ne 0) { throw "Fallo al compilar $proyecto" }
}
} finally {
    [IO.File]::WriteAllText($infoAtrio, $infoOriginal)
}

Push-Location $temporal
# Lo mismo que hace la compilacion oficial de Playnite con estos archivos.
Remove-Item 'System.Management.Automation.dll', 'Windows.winmd', 'CefSharp.BrowserSubprocess.exe', 'crash_reporter.cfg' -Force -ErrorAction SilentlyContinue
Get-ChildItem -Filter '*.pdb' | Remove-Item -Force

# Solo cambia el nombre del archivo. La identidad del ensamblado sigue siendo la de Playnite,
# que es lo que referencian los complementos y los temas.
$nombres = @{ 'Playnite.DesktopApp' = 'Atrio'; 'Playnite.FullscreenApp' = 'Atrio.Fullscreen'; 'Playnite.BrowserProcess' = 'Atrio.Browser' }
foreach ($viejo in $nombres.Keys) {
    foreach ($extension in 'exe', 'exe.config') {
        if (Test-Path "$viejo.$extension") { Move-Item "$viejo.$extension" "$($nombres[$viejo]).$extension" -Force }
    }
}
& $python (Join-Path $raiz 'atrio\herramientas\arreglar_config.py') 'Atrio.exe.config' 'Atrio.Fullscreen.exe.config'
Pop-Location

& $python (Join-Path $raiz 'atrio\herramientas\temas.py') (Join-Path $temporal 'Themes\Desktop') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Fallo al generar los temas' }
& $python (Join-Path $raiz 'atrio\herramientas\complementos.py') 'instalar' $temporal | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Fallo al preparar los complementos' }

Copy-Item (Join-Path $raiz 'atrio\LEEME.txt') (Join-Path $temporal 'LEEME.txt') -Force
Copy-Item (Join-Path $raiz 'LICENSE.md') (Join-Path $temporal 'LICENSE.md') -Force

# Lo que es de esta versión: al actualizar, lo que deje de estar aquí se retira. Nada más se toca.
$lista = Join-Path $temporal 'atrio-archivos.txt'
$inicio = (Resolve-Path $temporal).Path.TrimEnd('\').Length + 1
Get-ChildItem $temporal -Recurse -File | ForEach-Object { $_.FullName.Substring($inicio) } | Set-Content $lista -Encoding UTF8
Add-Content $lista 'atrio-archivos.txt' -Encoding UTF8

# Copia solo el programa: library, ExtensionsData y la configuracion del usuario no estan aqui.
Copy-Item (Join-Path $temporal '*') $Salida -Recurse -Force
Write-Output "listo: $Salida"

if ($Zip) {
    $nombreVersion = if ($Version -ne '') { $Version } else { [regex]::Match($infoOriginal, $patronVersion).Groups[1].Value }
    $archivo = Join-Path $raiz "salida\Atrio-$nombreVersion.zip"
    if (Test-Path $archivo) { Remove-Item $archivo -Force }
    Compress-Archive -Path (Join-Path $temporal '*') -DestinationPath $archivo
    Write-Output "paquete: $archivo"
}
Remove-Item $temporal -Recurse -Force
