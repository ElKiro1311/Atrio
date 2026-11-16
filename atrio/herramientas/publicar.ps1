# Publica una versión en las Releases de GitHub, donde Atrio la encuentra solo.
# Compila desde esta copia, que tiene que coincidir con main en GitHub. Lo que se sube es lo que hay en el repositorio.
# Antes hay que escribir lo que cambia en atrio\novedades\<version>.md y subirlo a main.
#
#   powershell -ExecutionPolicy Bypass -File atrio\herramientas\publicar.ps1 -Version 1.2.0
#
#   -Version   La etiqueta de la Release: x.y.z-beta (desde la 1.12.2; la 1.12.2 misma salio sin la fase para que la
#              vieran las copias de antes, que solo entienden x.y.z). El programa lleva solo el numero.
#   -Borrador  La deja como borrador: nadie la ve hasta que se publica (gh release edit 1.12.3-beta --draft=false).
#              Es lo que hace la compilación de GitHub (.github\workflows\publicar.yml), para comprobarla antes.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [switch]$Borrador
)
$ErrorActionPreference = 'Stop'

# Siempre explícito: esta copia también tiene como remoto el proyecto del que viene.
$repositorio = 'ElKiro1311/Atrio'
$raiz = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Push-Location $raiz
try {
    if ($Version -notmatch '^\d+\.\d+\.\d+(-(alfa|beta))?$') { throw "Versión no válida: $Version" }
    $numero = $Version -replace '-.*$', ''
    $novedades = "atrio\novedades\$numero.md"
    if (-not (Test-Path $novedades)) { throw "Falta $novedades" }
    if (git status --porcelain) { throw 'Hay cambios sin confirmar.' }

    git fetch -q origin main
    if ((git rev-parse HEAD) -ne (git rev-parse origin/main)) { throw 'Esta copia no coincide con main en GitHub.' }

    # Solo avanza: Atrio ignora cualquier versión que no sea mayor que la suya.
    $publicadas = gh release list --repo $repositorio --limit 100 --json tagName --jq '.[].tagName' |
        Where-Object { $_ -match '^v?\d+\.\d+\.\d+(-(alfa|beta))?$' } | ForEach-Object { [version]($_.TrimStart('v') -replace '-.*$', '') } | Sort-Object
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo leer la lista de versiones.' }
    if ($publicadas -and [version]$numero -le ($publicadas | Select-Object -Last 1)) {
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

    # Titulo: la version con su fase y el tema (la primera linea de las novedades): «1.12.3-beta: ...».
    $fase = [regex]::Match([IO.File]::ReadAllText((Join-Path $raiz 'source\Playnite\Atrio\AtrioInfo.cs')), 'const string Phase = "([^"]*)"').Groups[1].Value
    $etiqueta = if ($Version -match '-') { $Version } elseif ($fase) { "$numero-$fase" } else { $numero }
    $tema = ((Get-Content $novedades -Encoding UTF8 | Where-Object { $_.Trim() } | Select-Object -First 1) -replace '^#+\s*', '').Trim().TrimEnd('.')
    $titulo = if ($tema) { "${etiqueta}: $tema" } else { $etiqueta }
    $opciones = @($Version, $paquete, $descarga, '--repo', $repositorio, '--target', (git rev-parse HEAD), '--title', $titulo, '--notes-file', $novedades)
    if ($Borrador) { $opciones += '--draft' }
    gh release create @opciones
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo crear la versión en GitHub.' }
}
finally {
    Pop-Location
}
