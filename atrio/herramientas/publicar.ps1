# Publica una versión en las Releases de GitHub, donde Atrio la encuentra solo.
# Compila desde esta copia, que tiene que coincidir con main en GitHub. Lo que se sube es lo que hay en el repositorio.
# Antes hay que escribir lo que cambia en atrio\novedades\<version>.md y subirlo a main.
#
#   powershell -ExecutionPolicy Bypass -File atrio\herramientas\publicar.ps1 -Version 1.2.0
param(
    [Parameter(Mandatory = $true)][string]$Version
)
$ErrorActionPreference = 'Stop'

# Siempre explícito: esta copia también tiene como remoto el proyecto del que viene.
$repositorio = 'ElKiro1311/Atrio'
$raiz = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Push-Location $raiz
try {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Versión no válida: $Version" }
    $novedades = "atrio\novedades\$Version.md"
    if (-not (Test-Path $novedades)) { throw "Falta $novedades" }
    if (git status --porcelain) { throw 'Hay cambios sin confirmar.' }

    git fetch -q origin main
    if ((git rev-parse HEAD) -ne (git rev-parse origin/main)) { throw 'Esta copia no coincide con main en GitHub.' }

    # Solo avanza: Atrio ignora cualquier versión que no sea mayor que la suya.
    $publicadas = gh release list --repo $repositorio --limit 100 --json tagName --jq '.[].tagName' |
        Where-Object { $_ -match '^v\d+\.\d+\.\d+$' } | ForEach-Object { [version]$_.TrimStart('v') } | Sort-Object
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo leer la lista de versiones.' }
    if ($publicadas -and [version]$Version -le ($publicadas | Select-Object -Last 1)) {
        throw "La última versión publicada ya es $($publicadas | Select-Object -Last 1)."
    }

    & (Join-Path $PSScriptRoot 'compilar.ps1') -Zip -Version $Version
    # Dos paquetes con lo mismo: el de las actualizaciones, sin carpeta, que es el que buscan las versiones
    # instaladas, y el de descargar a mano, con todo dentro de una carpeta Atrio.
    $paquete = "salida\Atrio-$Version.zip"
    $descarga = 'salida\Atrio.zip'
    foreach ($archivo in @($paquete, $descarga)) {
        if (-not (Test-Path $archivo)) { throw "No se ha creado $archivo" }
    }

    gh release create "v$Version" $paquete $descarga --repo $repositorio --target (git rev-parse HEAD) --title "Atrio $Version" --notes-file $novedades
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo crear la versión en GitHub.' }
}
finally {
    Pop-Location
}
