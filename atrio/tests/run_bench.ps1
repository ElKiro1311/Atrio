# Runs a bench build (build.ps1 -Bench) off screen and follows a script of commands.
# The commands are in source\Playnite.DesktopApp\Atrio\AtrioCaptura.cs.
#
#   run_bench.ps1 -Exe output\Bench\Atrio.exe -Data <data folder> -Script script.txt -Captures <folder>
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][Alias('Datos')][string]$Data,
    [Parameter(Mandatory = $true)][Alias('Guion')][string]$Script,
    [Parameter(Mandatory = $true)][Alias('Capturas')][string]$Captures,
    [Alias('Limite')][int]$Limit = 150,
    [Alias('ConSincronizacion')][switch]$WithSync
)
New-Item -ItemType Directory -Force $Captures | Out-Null
Remove-Item (Join-Path $Captures 'guion.log') -ErrorAction SilentlyContinue
Copy-Item $Script (Join-Path $Captures 'guion.txt') -Force
$env:ATRIO_CAPTURA = $Captures
$arguments = @('--userdatadir', "`"$Data`"", '--hidesplashscreen')
if (-not $WithSync) { $arguments += '--nolibupdate' }
$process = Start-Process -FilePath $Exe -ArgumentList $arguments -PassThru
if (-not $process.WaitForExit($Limit * 1000)) {
    Write-Output 'TIME OUT, closing'
    $process.Kill()
}
Write-Output "exit code: $($process.ExitCode)"
$log = Join-Path $Captures 'guion.log'
if (Test-Path $log) { Get-Content $log -Tail 30 }
