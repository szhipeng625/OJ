#!/bin/bash
# 中间层部署后验证脚本
echo "=== generator/exe endpoint (expect HTTP 400 auth error, not 404) ==="
curl -sk -o /dev/null -w "HTTP %{http_code}\n" -X POST https://127.0.0.1:8899/api/generator/exe

echo "=== problem detail new fields ==="
curl -sk "https://127.0.0.1:8899/api/problem?id=111" | grep -o -E '"stdExe"|"spjCode"|"spjExe"|"exe"' | sort -u

echo "=== DB columns ==="
mysql -h43.157.39.171 -uoj_user -p12345678 oj -N -e \
 "SHOW COLUMNS FROM problems LIKE 'std_exe'; SHOW COLUMNS FROM problems LIKE 'spj_code'; SHOW COLUMNS FROM problems LIKE 'spj_exe'; SHOW COLUMNS FROM generators LIKE 'exe';"

echo "=== DONE ==="
