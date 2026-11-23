# Makes the themes' previews and the README's images again, with the made-up library of atrio\demo.
# It needs PowerShell 7 and a bench build (build.ps1 -Bench).
#
#   theme_previews.ps1 -Exe output\Bench\Atrio.exe -Work <temporary folder> [-Language en_US]
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][Alias('Trabajo')][string]$Work,
    [string]$Language = 'en_US'
)
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$run = Join-Path $PSScriptRoot 'run_bench.ps1'
$art = Join-Path $Work 'art'
$data = Join-Path $Work 'data'
$captures = Join-Path $Work 'captures'
$script = Join-Path $Work 'script.txt'
if (Test-Path $data) { Remove-Item $data -Recurse -Force }
New-Item -ItemType Directory -Force $data, $captures | Out-Null

& python (Join-Path $root 'atrio\demo\generate.py') $art $Language.Substring(0, 2)
$env:ATRIO_IMPORTAR = 'no'

# These images are published: the stores come out with badges of our own, without their logos, the shelves with real games
# stay empty and no new version is announced.
$env:ATRIO_SIN_MARCAS = 'si'
$env:ATRIO_SIN_RED = 'si'
$env:ATRIO_VERSION = '99.0'
$env:ATRIO_CAPTURA_ANCHO = 1500
$env:ATRIO_CAPTURA_ALTO = 900

"espera 1500`ndemo $art`nespera 1500`nsalir" | Set-Content $script -Encoding utf8
& $run -Exe $Exe -Data $data -Script $script -Captures $captures -Limit 120 | Select-Object -Last 3

$themes = 'Atrio_Noche:AtrioNoche', 'Atrio_Aurora:AtrioAurora', 'Atrio_OLED:AtrioOLED', 'Atrio_Cobalto:Cobalto', 'Atrio_Oliva:Oliva',
         'Atrio_Grafito:Grafito', 'Atrio_GrafitoCompacto:GrafitoCompacto', 'Playnite_builtin_DefaultDesktop:Default'
$configuration = Join-Path $data 'config.json'
foreach ($pair in $themes) {
    $id, $folder = $pair -split ':'
    $json = Get-Content $configuration -Raw | ConvertFrom-Json -AsHashtable
    $json['Theme'] = $id
    $json['Language'] = $Language
    $view = 'Grid'
    $sidebar = 'Left'
    $info = Join-Path (Split-Path $Exe) "Themes\Desktop\$folder\atrio-theme.yaml"
    if (Test-Path $info) {
        foreach ($line in Get-Content $info) {
            if ($line -match '^View: (.+)$') { $view = $Matches[1] }
            if ($line -match '^Sidebar: (.+)$') { $sidebar = $Matches[1] }
        }
    }
    $json['ViewSettings']['GamesViewType'] = $view
    $json['SidebarPosition'] = $sidebar
    $json | ConvertTo-Json -Depth 30 | Set-Content $configuration -Encoding utf8
    "espera 3500`ncaptura theme-$folder-home`nbarra $(if ($Language -like 'es*') { 'Biblioteca' } else { 'Library' })`nespera 1500`njuego Aguja del Norte`nespera 2800`ncaptura theme-$folder-library`nsalir" | Set-Content $script -Encoding utf8
    & $run -Exe $Exe -Data $data -Script $script -Captures $captures -Limit 60 | Select-Object -Last 1
    Write-Output "theme $folder ready"
}

Remove-Item Env:ATRIO_IMPORTAR, Env:ATRIO_SIN_MARCAS, Env:ATRIO_SIN_RED, Env:ATRIO_VERSION, Env:ATRIO_CAPTURA_ANCHO, Env:ATRIO_CAPTURA_ALTO
& python (Join-Path $root 'atrio\tools\make_previews.py') $captures $Language.Substring(0, 2)
