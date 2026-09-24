# Reproduce the client's startup sync to capture the actual error returned by ojcore.dll
$ErrorActionPreference = "Stop"

$dll = "D:/OJ/client/bin/Debug/native/ojcore.dll"
$problemDir = "D:/OJ/client/bin/Debug/problems"
$dataDir = "D:/OJ/client/bin/Debug/ojdata"
$mwUrl = "https://47.253.41.10:8899"

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class OJTest {
    [DllImport("$dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int oj_init_middleware([MarshalAs(UnmanagedType.LPUTF8Str)] string url,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string problem_dir,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string data_dir);
    [DllImport("$dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr oj_mysql_sync_problems();
    [DllImport("$dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void oj_free_string(IntPtr s);
}
"@

"init_middleware -> " + [OJTest]::oj_init_middleware($mwUrl, $problemDir, $dataDir)
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$p = [OJTest]::oj_mysql_sync_problems()
$sw.Stop()
# Read raw UTF-8 bytes from the returned buffer before freeing
$len = 0
while ([Runtime.InteropServices.Marshal]::ReadByte($p, $len) -ne 0) { $len++ }
$bytes = New-Object byte[] $len
[Runtime.InteropServices.Marshal]::Copy($p, $bytes, 0, $len)
[OJTest]::oj_free_string($p)
$json = [System.Text.Encoding]::UTF8.GetString($bytes)
"sync took $($sw.ElapsedMilliseconds) ms"
"sync result:"
$json
