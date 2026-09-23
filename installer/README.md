# OJ 一键安装器

一个**下载 + 安装 + 卸载**三合一的单文件 exe（自包含，目标机器无需装 .NET）。

## 工作原理

1. 运行 `OJ安装器.exe`（双击，无参数）→ 从服务器下载 `oj-package.zip`。
2. 解压到 `%LocalAppData%\OJ`（无需管理员），结构为 `client\` 与 `server\`。
3. 创建桌面 / 开始菜单快捷方式：`OJ客户端`、`OJ服务端`、`卸载 OJ`。
4. 写入卸载注册表项，并把自身复制为 `uninstall.exe`。
5. 点 `卸载 OJ` 或运行 `uninstall.exe /uninstall` → 结束进程、删快捷方式、删注册表、删安装目录（含自身）。

## 打包（生成最终 exe + 安装包）

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\make_installer.ps1 -BaseUrl "http://你的服务器/oj/"
```

产物在 `installer_out\`：
- `oj-package.zip` —— 上传到服务器（`BaseUrl` 指向的目录）
- `oj-package.zip.sha256` —— 一并上传（可选，用于校验）
- `OJ安装器.exe` —— 发给用户

> `-BaseUrl` 默认是占位符 `http://YOUR-SERVER/oj/`，也可用 `-NoBuild` 跳过重新构建（当产物已是最新时）。

## 上传到服务器

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\upload_package.ps1 -HostName 服务器IP -UserName root -RemoteDir /var/www/html/oj/
```

上传后确保 `http://服务器/oj/oj-package.zip` 能直接下载（nginx 静态目录 / 对象存储均可）。

## 更换下载地址而不重新编译

安装器按以下优先级读取下载地址：
1. 命令行 `/url=...`
2. exe 同目录下的 `installer_url.txt`（内容为地址前缀）
3. 打包时烧录的默认地址

## 说明

- 客户端 / 服务端为框架依赖发布，目标机器需安装 **.NET 8 桌面运行时**；安装器会检测并在缺失时提示跳转下载页。
- 默认安装到 `%LocalAppData%\OJ`（每用户、免 UAC）；可用 `/dir=<目录>` 自定义。
