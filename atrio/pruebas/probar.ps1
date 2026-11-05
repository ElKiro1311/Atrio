# Ejecuta una compilacion de prueba (compilar.ps1 -Banco) fuera de pantalla y sigue un guion de ordenes.
# Las ordenes estan en source\Playnite.DesktopApp\Atrio\AtrioCaptura.cs.
#
#   probar.ps1 -Exe salida\Banco\Atrio.exe -Datos <carpeta de datos> -Guion guion.txt -Capturas <carpeta>
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Datos,
    [Parameter(Mandatory = $true)][string]$Guion,
    [Parameter(Mandatory = $true)][string]$Capturas,
    [int]$Limite = 150,
    [switch]$ConSincronizacion
)
New-Item -ItemType Directory -Force $Capturas | Out-Null
Remove-Item (Join-Path $Capturas 'guion.log') -ErrorAction SilentlyContinue
Copy-Item $Guion (Join-Path $Capturas 'guion.txt') -Force
$env:ATRIO_CAPTURA = $Capturas
$argumentos = @('--userdatadir', "`"$Datos`"", '--hidesplashscreen')
if (-not $ConSincronizacion) { $argumentos += '--nolibupdate' }
$proceso = Start-Process -FilePath $Exe -ArgumentList $argumentos -PassThru
if (-not $proceso.WaitForExit($Limite * 1000)) {
    Write-Output 'TIEMPO AGOTADO, cerrando'
    $proceso.Kill()
}
Write-Output "codigo de salida: $($proceso.ExitCode)"
$registro = Join-Path $Capturas 'guion.log'
if (Test-Path $registro) { Get-Content $registro -Tail 30 }
