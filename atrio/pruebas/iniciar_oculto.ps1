# Abre un programa en un escritorio de Windows aparte: corre de verdad, pero nadie ve sus ventanas.
# Sirve para probar una compilacion normal sin banco de pruebas y sin molestar en la pantalla.
#
#   iniciar_oculto.ps1 -Exe <programa> -Argumentos '--userdatadir "..."' -Segundos 40
# Devuelve el codigo de salida, o cierra el proceso si sigue abierto al terminar el tiempo.
# -EsperaFinal mantiene el escritorio ese tiempo: lo que el programa abra al cerrarse necesita que siga existiendo.
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$Argumentos = '',
    [int]$Segundos = 40,
    [string]$ParaCerrar = '',
    [int]$EsperaFinal = 0,
    [string]$Directorio = ''
)
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class EscritorioAparte
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, int flags, uint access, IntPtr attributes);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateProcess(string application, string commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string directory, ref STARTUPINFO startup, out PROCESS_INFORMATION information);

    public static int Start(string desktopName, string exe, string arguments, string directory)
    {
        var startup = new STARTUPINFO();
        startup.cb = Marshal.SizeOf(typeof(STARTUPINFO));
        startup.lpDesktop = desktopName;
        PROCESS_INFORMATION information;
        var line = "\"" + exe + "\" " + arguments;
        if (!CreateProcess(null, line, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, directory, ref startup, out information))
        {
            throw new System.ComponentModel.Win32Exception();
        }

        return information.dwProcessId;
    }
}
'@

$nombre = 'AtrioPrueba' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$escritorio = [EscritorioAparte]::CreateDesktop($nombre, [IntPtr]::Zero, [IntPtr]::Zero, 0, [uint32]'0x10000000', [IntPtr]::Zero)
if ($escritorio -eq [IntPtr]::Zero) { throw 'No se pudo crear el escritorio aparte' }

# -Directorio: otra carpeta de trabajo, como la de un inicio con Windows (System32).
$carpetaTrabajo = if ($Directorio -ne '') { $Directorio } else { Split-Path $Exe }
$id = [EscritorioAparte]::Start($nombre, $Exe, $Argumentos, $carpetaTrabajo)
$proceso = Get-Process -Id $id -ErrorAction SilentlyContinue
Write-Output "proceso $id en el escritorio $nombre"
$terminado = $false
if ($proceso) { $terminado = $proceso.WaitForExit($Segundos * 1000) }

if (-not $terminado -and $proceso) {
    if ($ParaCerrar -ne '') {
        [EscritorioAparte]::Start($nombre, $Exe, $ParaCerrar, (Split-Path $Exe)) | Out-Null
        $terminado = $proceso.WaitForExit(15000)
    }
    if (-not $terminado) {
        Write-Output 'seguia abierto: se cierra a la fuerza'
        $proceso.Kill()
    }
}
Start-Sleep -Milliseconds 500
if ($EsperaFinal -gt 0) { Start-Sleep -Seconds $EsperaFinal }
[EscritorioAparte]::CloseDesktop($escritorio) | Out-Null
Write-Output "terminado: $terminado"
