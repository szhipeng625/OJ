# Verify authorcore Chinese generator name fix
$ErrorActionPreference = "Stop"
$dll = "D:/OJ/author/authorcore/bin/Debug/authorcore.dll"
$root = "$env:TEMP\oj_ac_test\problems"
$temp = "$env:TEMP\oj_ac_test\temp"
New-Item -ItemType Directory -Path "$root\111" -Force | Out-Null
New-Item -ItemType Directory -Path $temp -Force | Out-Null

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class AC {
    [DllImport("$dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int ac_init([MarshalAs(UnmanagedType.LPUTF8Str)] string root);
    [DllImport("$dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void ac_gen_set_temp([MarshalAs(UnmanagedType.LPUTF8Str)] string temp);
    [DllImport("$dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ac_gen_save(int id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string code);
    [DllImport("$dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ac_gen_compile(int id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("$dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ac_gen_run(int id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int n);
    [DllImport("$dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ac_gen_list(int id);
    [DllImport("$dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void ac_free_string(IntPtr s);
}
"@

function Take($p) {
    $len = 0
    while ([Runtime.InteropServices.Marshal]::ReadByte($p, $len) -ne 0) { $len++ }
    $b = New-Object byte[] $len
    [Runtime.InteropServices.Marshal]::Copy($p, $b, 0, $len)
    [AC]::ac_free_string($p)
    return [System.Text.Encoding]::UTF8.GetString($b)
}

[AC]::ac_init($root) | Out-Null
[AC]::ac_gen_set_temp($temp)

$code = @"
#include <bits/stdc++.h>
using namespace std;
int main(int argc, char** argv){
    int seed = (argc>2)?atoi(argv[2]):1;
    mt19937 rng(seed);
    uniform_int_distribution<int> d(1,100);
    int n = d(rng);
    cout << n << "\n";
    for(int i=0;i<n;i++) cout << (i?" ":"") << d(rng);
    cout << "\n";
    return 0;
}
"@

# 避免 .ps1 编码歧义，直接用 UTF-8 字节构造 "测64"
$name = [System.Text.Encoding]::UTF8.GetString([byte[]]@(0xE6,0xB5,0x8B,0x36,0x34))

"save: " + (Take([AC]::ac_gen_save(111, $name, $code)))
"compile: " + (Take([AC]::ac_gen_compile(111, $name)))
"run: " + (Take([AC]::ac_gen_run(111, $name, 3)))
"list: " + (Take([AC]::ac_gen_list(111)))
""
"=== filesystem ==="
Get-ChildItem $root -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName.Replace($root,'') }
