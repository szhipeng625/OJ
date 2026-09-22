# MySQL 用户层接入指南

ojcore.dll 已支持可选的 MySQL 后端，存储用户账号、密码哈希、角色和提交记录。
**未配置 MySQL 时自动回退本地 anonymous 模式，现有功能不受影响。**

## 一、准备 MySQL

1. 安装 [MySQL Community Server 8.0+](https://dev.mysql.com/downloads/mysql/)
2. 安装 [MySQL Connector/C 8.0](https://dev.mysql.com/downloads/connector/c/)（运行时需要 `libmysql.dll`）
3. 启动 MySQL 服务，建库建表：
   ```
   mysql -u root -p < docs/setup.sql
   ```
   这会创建 `oj` 库和 users / sessions / submissions 三张表。

## 二、配置客户端

把 `docs/mysql_config.example.json` 复制到客户端与出题服务端的运行目录（`dist/mysql_config.json`、`dist/author/mysql_config.json`），改成真实连接：
```json
{
  "host": "你的服务器IP",
  "port": 3306,
  "user": "root",
  "pass": "你的密码",
  "db": "oj",
  "redisHost": "你的LSM服务器IP",
  "redisPort": 6379
}
```
把 Connector/C 里的 `libmysql.dll` 复制到两个目录。
- 首次连接时若 `oj` 库不存在，会自动 `CREATE DATABASE oj`（无需手工建库）。
- 出题端与服务端共用同一份连接参数：出题端把发布的题目写入该库，客户端从该库拉取题目。
- `redisHost` / `redisPort` 指向远端 LSM 存储服务（RESP 协议）；`redisHost` 缺省与 `host` 同机，`redisPort` 默认 6379。

## 三、使用

启动 `dist/client.exe`：
- 若检测到 `mysql_config.json` 且 `libmysql.dll` 可加载，会先弹登录窗
- 首次使用可点"注册"，角色选 `admin` / `author` / `user`
- 登录态保存在 `ojdata/session.txt`，7 天内免登录
- 顶部蓝色徽章显示 `👤 用户名 [角色]`
- 不删 `mysql_config.json` 就不启用 MySQL；删掉则回到本地模式

## 四、判题结果落库（最后提交原地更新）

- 每次判题结束后，结果除写入远端 LSM 存储外，还会 upsert 到 MySQL 的 `submissions` 表。
- `submissions` 上有唯一键 `uq_user_problem (user_id, problem_id, contest_id)`，
  配合 `INSERT ... ON DUPLICATE KEY UPDATE`：**同一用户对同一题重复提交时，
  最后一次结果原地覆盖旧行**（verdict / detail / time_ms / virtual / created_at 全部更新），
  表中每人每题只保留一条最新记录，不会无限追加。
- 登录用户按账号归属；未登录提交统一归到 `anonymous` 默认用户（建表时自动创建，空哈希无法登录）。
- 练习提交 `contest_id=0`；老库（`contest_id` 可空、无唯一键）在客户端启动执行
  `oj_mysql_init_schema` 时自动幂等迁移（NULL→0、去重保留最新、补唯一键）。

## 五、权限

| 角色    | 能做什么 |
|---------|---------|
| admin   | 登录、参赛、看全部用户（`oj_list_users`） |
| author  | 登录、参赛 |
| user    | 登录、参赛 |

密码用 SHA-256(salt + password) 哈希存储，salt 每用户随机 16 字节。
SQL 注入通过 `mysql_real_escape_string` 转义。

## 六、题目与比赛分发（出题端上传、客户端拉取）

- 出题端在「发布」题目/比赛时，除复制到本地客户端目录外，还会把内容写入 MySQL：
  - `problems` 表：题面（标题/描述/样例）、元数据（时限/内存/标签）、标程源码 `std_code`（**AES-256 密文**）。
  - `generators` 表：生成器源码（**AES-256 密文**）+ 描述；`problem_generators` 表：题目 ↔ 生成器关联（目录名 + 测试点数量 `gen_count` + 确定性种子 `seed_base`）。一道题一个标程、多个生成器，生成器可跨题复用。
  - `contests` 表：`contest.json` 原文。
- 客户端启用 MySQL 后，启动时从 MySQL 拉取题目/比赛，**解密**生成器与标程源码到本地，并在本地**编译生成器 → 运行生成 `.in` → 编译标程 → 运行生成 `.out`**。
  - 用「生成器内容 + 组数 + 种子 + 标程」做哈希做缓存：本地已存在且内容未变则**不再拉取、不再生成**，直接判题。
  - 所有客户端用同一 `seed_base`，生成的判题数据完全一致。
- 效果：数据库只存几 KB 的源码（还是密文），不存可能上 GB 的测试数据；判题数据在客户端本地按需生成并缓存。

## 七、新导出函数（ojcore.dll）

- `oj_init_mysql(host, port, user, pass, db, problem_dir)` — 0 = 已连 MySQL；1 = 回退本地
- `oj_mysql_init_schema()` — 建表（幂等）
- `oj_register(u, p, role)`
- `oj_login(u, p)` — 返回 token
- `oj_whoami(token)` — 校验会话
- `oj_logout(token)`
- `oj_list_users(token)` — 仅 admin
