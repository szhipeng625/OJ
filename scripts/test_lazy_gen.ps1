# Verify lazy data generation: sync must NOT generate data; ensure_problem_data must.
$ErrorActionPreference = "Stop"
$dll = "D:/OJ/client/bin/Debug/native/ojcore.dll"
$base = "$env:TEMP\oj_lazy_test"
$problemDir = "$base\problems"
$dataDir = "$base\ojdata"
New-Item -ItemType Directory -Path $problemDir -Force | Out-Null
New-Item -ItemType Directory -Path $dataDir -Force | Out-Null

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class OJ {
    [DllImport("$dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int oj_init_middleware([MarshalAs(UnmanagedType.LPUTF8Str)] string url,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string problem_dir,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string data_dir);
    [DllImport("$dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr oj_mysql_sync_problems();
    [DllImport("$dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr oj_ensure_problem_data(int problem_id);
    [DllImport("$dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void oj_free_string(IntPtr s);
}
"@

function Take($p) {
    $len = 0
    while ([Runtime.InteropServices.Marshal]::ReadByte($p, $len) -ne 0) { $len++ }
    $b = New-Object byte[] $len
    [Runtime.InteropServices.Marshal]::Copy($p, $b, 0, $len)
    [OJ]::oj_free_string($p)
    return [System.Text.Encoding]::UTF8.GetString($b)
}

[OJ]::oj_init_middleware("https://47.253.41.10:8899", $problemDir, $dataDir) | Out-Null

"=== 1. sync (should NOT generate data) ==="
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$syncRes = Take([OJ]::oj_mysql_sync_problems())
$sw.Stop()
"sync took $($sw.ElapsedMilliseconds) ms -> $syncRes"
""
"=== files after sync (expect NO .in/.out, only gen.cpp/genmeta.txt) ==="
Get-ChildItem $problemDir -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName.Replace($problemDir,'') }
""
"=== 2. ensure_problem_data(111) (should generate data) ==="
$sw2 = [System.Diagnostics.Stopwatch]::StartNew()
$ens = Take([OJ]::oj_ensure_problem_data(111))
$sw2.Stop()
"ensure took $($sw2.ElapsedMilliseconds) ms -> $ens"
""
"=== files after ensure (expect .in/.out + .genhash) ==="
Get-ChildItem $problemDir -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName.Replace($problemDir,'') }
""
"=== 3. ensure again (should be cached/fast) ==="
$sw3 = [System.Diagnostics.Stopwatch]::StartNew()
$ens2 = Take([OJ]::oj_ensure_problem_data(111))
$sw3.Stop()
"ensure2 took $($sw3.ElapsedMilliseconds) ms -> $ens2"
