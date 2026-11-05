# Rehace las vistas previas de los temas con la biblioteca inventada de atrio\demo.
# Necesita PowerShell 7 y una compilacion de prueba (compilar.ps1 -Banco).
#
#   vistas_temas.ps1 -Exe salida\Banco\Atrio.exe -Trabajo <carpeta temporal>
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Trabajo
)
$raiz = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$probar = Join-Path $PSScriptRoot 'probar.ps1'
$arte = Join-Path $Trabajo 'arte'
$datos = Join-Path $Trabajo 'datos'
$capturas = Join-Path $Trabajo 'capturas'
$guion = Join-Path $Trabajo 'guion.txt'
if (Test-Path $datos) { Remove-Item $datos -Recurse -Force }
New-Item -ItemType Directory -Force $datos, $capturas | Out-Null

& python (Join-Path $raiz 'atrio\demo\generar.py') $arte
$env:ATRIO_IMPORTAR = 'no'
$env:ATRIO_CAPTURA_ANCHO = 1500
$env:ATRIO_CAPTURA_ALTO = 900

"espera 1500`ndemo $arte`nespera 1500`nsalir" | Set-Content $guion -Encoding utf8
& $probar -Exe $Exe -Datos $datos -Guion $guion -Capturas $capturas -Limite 120 | Select-Object -Last 3

$temas = 'Atrio_Noche:AtrioNoche', 'Atrio_Aurora:AtrioAurora', 'Atrio_OLED:AtrioOLED', 'Atrio_Cobalto:Cobalto', 'Atrio_Oliva:Oliva',
         'Atrio_Grafito:Grafito', 'Atrio_GrafitoCompacto:GrafitoCompacto', 'Playnite_builtin_DefaultDesktop:Default'
$configuracion = Join-Path $datos 'config.json'
foreach ($par in $temas) {
    $id, $carpeta = $par -split ':'
    $json = Get-Content $configuracion -Raw | ConvertFrom-Json -AsHashtable
    $json['Theme'] = $id
    $json['Language'] = 'es_ES'
    $vista = 'Grid'
    $barra = 'Left'
    $info = Join-Path (Split-Path $Exe) "Themes\Desktop\$carpeta\atrio-tema.yaml"
    if (Test-Path $info) {
        foreach ($linea in Get-Content $info) {
            if ($linea -match '^Vista: (.+)$') { $vista = $Matches[1] }
            if ($linea -match '^BarraLateral: (.+)$') { $barra = $Matches[1] }
        }
    }
    $json['ViewSettings']['GamesViewType'] = $vista
    $json['SidebarPosition'] = $barra
    $json | ConvertTo-Json -Depth 30 | Set-Content $configuracion -Encoding utf8
    "espera 3500`ncaptura tema-$carpeta-inicio`nbarra Biblioteca`nespera 2800`ncaptura tema-$carpeta-biblioteca`nsalir" | Set-Content $guion -Encoding utf8
    & $probar -Exe $Exe -Datos $datos -Guion $guion -Capturas $capturas -Limite 60 | Select-Object -Last 1
    Write-Output "tema $carpeta listo"
}

Remove-Item Env:ATRIO_IMPORTAR, Env:ATRIO_CAPTURA_ANCHO, Env:ATRIO_CAPTURA_ALTO
& python (Join-Path $raiz 'atrio\herramientas\hacer_vistas.py') $capturas
