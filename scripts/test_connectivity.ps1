# Connectivity & stability test for OJ middleware (HTTP mode)
$ErrorActionPreference = "Continue"
$MW = "http://47.253.41.10:8899"
$user = "conn_test_" + (Get-Date -Format "MMddHHmmss")
$pass = "test123456"

function Post-Json($path, $obj, $token) {
    $file = "$env:TEMP\oj_body.json"
    ($obj | ConvertTo-Json -Compress) | Set-Content -Path $file -Encoding ascii -NoNewline
    if ($token) {
        return curl.exe -s -m 15 -X POST "$MW$path" -H "Content-Type: application/json" -H "Authorization: Bearer $token" --data-binary "@$file"
    } else {
        return curl.exe -s -m 15 -X POST "$MW$path" -H "Content-Type: application/json" --data-binary "@$file"
    }
}
function Get-Api($path, $token) {
    if ($token) {
        return curl.exe -s -m 15 "$MW$path" -H "Authorization: Bearer $token"
    } else {
        return curl.exe -s -m 15 "$MW$path"
    }
}

"========== 1. Register =========="
$r = Post-Json "/api/register" @{username=$user;password=$pass;role="user"}
$r
""

"========== 2. Login =========="
$login = Post-Json "/api/login" @{username=$user;password=$pass}
$login
$tok = ($login | ConvertFrom-Json).token
if (-not $tok) { "!! login failed, cannot proceed with authed tests"; exit 1 }
"token OK (len $($tok.Length))"
""

"========== 3. whoami =========="
Get-Api "/api/whoami" $tok
""

"========== 4. contests =========="
Get-Api "/api/contests" $null
""

"========== 5. contest?id=1 =========="
Get-Api "/api/contest?id=1" $null
""

"========== 6. LSM round-trip (set/get/del) =========="
$k = "conn_test_key"
$v = "hello_" + (Get-Date -Format "HHmmssfff")
Post-Json "/api/lsm/set" @{key=$k;value=$v} $tok
"set -> $v"
$g = Post-Json "/api/lsm/get" @{key=$k} $tok
$g
$got = ($g | ConvertFrom-Json).value
if ($got -eq $v) { "ROUND-TRIP OK: got '$got'" } else { "ROUND-TRIP MISMATCH: expected '$v' got '$got'" }
Post-Json "/api/lsm/del" @{key=$k} $tok
"del done"
Post-Json "/api/lsm/get" @{key=$k} $tok
"get-after-del (found should be false)"
""

"========== 7. LSM hash (hset/hget/hkeys) =========="
$hk = "conn_test_hash"
Post-Json "/api/lsm/hset" @{key=$hk;field="a";value="1"} $tok | Out-Null
Post-Json "/api/lsm/hset" @{key=$hk;field="b";value="2"} $tok | Out-Null
$hg = Post-Json "/api/lsm/hget" @{key=$hk;field="a"} $tok
"hget a -> $hg"
Post-Json "/api/lsm/hkeys" @{key=$hk} $tok
"hkeys above"
""

"========== 8. LSM incr =========="
$ik = "conn_test_incr"
Post-Json "/api/lsm/incr" @{key=$ik} $tok
Post-Json "/api/lsm/incr" @{key=$ik} $tok
Post-Json "/api/lsm/incr" @{key=$ik} $tok
"incr x3 above (expect 1,2,3)"
""

"========== 9. Unauthorized LSM (should fail ok=false) =========="
$file = "$env:TEMP\oj_body.json"
(@{key="x"} | ConvertTo-Json -Compress) | Set-Content -Path $file -Encoding ascii -NoNewline
curl.exe -s -m 15 -X POST "$MW/api/lsm/get" -H "Content-Type: application/json" --data-binary "@$file"
""
""

"========== 10. Stability: 20x contests + 20x lsm/get loop =========="
$fail = 0
for ($i=1; $i -le 20; $i++) {
    $c = curl.exe -s -o NUL -w "%{http_code}" -m 15 "$MW/api/contests"
    if ($c -ne "200") { $fail++; "contests iter $i -> HTTP $c" }
}
"contests loop: 20 requests, $fail failures"
$fail = 0
for ($i=1; $i -le 20; $i++) {
    $tmp = Post-Json "/api/lsm/get" @{key=$k} $tok
    $j = $tmp | ConvertFrom-Json
    if (-not $j.ok) { $fail++ }
}
"lsm/get loop: 20 requests, $fail failures"
""

"========== 11. Cleanup: logout =========="
curl.exe -s -m 15 -X POST "$MW/api/logout" -H "Content-Type: application/json" -H "Authorization: Bearer $tok" -d '{}'
""
"done. test user: $user"
