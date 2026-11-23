# Opens a program on a separate Windows desktop: it really runs, but nobody sees its windows.
# It's for testing a normal build without the test bench and without getting in the way on the screen.
#
#   start_hidden.ps1 -Exe <program> -Arguments '--userdatadir "..."' -Seconds 40
# It says whether it ended, or closes the process if it's still open when the time is up.
# -FinalWait keeps the desktop for that long: whatever the program opens on closing needs it to go on existing.
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Alias('Argumentos')][string]$Arguments = '',
    [Alias('Segundos')][int]$Seconds = 40,
    [Alias('ParaCerrar')][string]$ToClose = '',
    [Alias('EsperaFinal')][int]$FinalWait = 0,
    [Alias('Directorio')][string]$Directory = ''
)
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SeparateDesktop
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

$name = 'AtrioTest' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$desktop = [SeparateDesktop]::CreateDesktop($name, [IntPtr]::Zero, [IntPtr]::Zero, 0, [uint32]'0x10000000', [IntPtr]::Zero)
if ($desktop -eq [IntPtr]::Zero) { throw 'The separate desktop could not be created' }

# -Directory: another working folder, such as the one when starting with Windows (System32).
$workFolder = if ($Directory -ne '') { $Directory } else { Split-Path $Exe }
$id = [SeparateDesktop]::Start($name, $Exe, $Arguments, $workFolder)
$process = Get-Process -Id $id -ErrorAction SilentlyContinue
Write-Output "process $id on the desktop $name"
$ended = $false
if ($process) { $ended = $process.WaitForExit($Seconds * 1000) }

if (-not $ended -and $process) {
    if ($ToClose -ne '') {
        [SeparateDesktop]::Start($name, $Exe, $ToClose, (Split-Path $Exe)) | Out-Null
        $ended = $process.WaitForExit(15000)
    }
    if (-not $ended) {
        Write-Output 'it was still open: closing it by force'
        $process.Kill()
    }
}
Start-Sleep -Milliseconds 500
if ($FinalWait -gt 0) { Start-Sleep -Seconds $FinalWait }
[SeparateDesktop]::CloseDesktop($desktop) | Out-Null
Write-Output "ended: $ended"
