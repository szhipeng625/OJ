-- OJ 数据库初始化脚本
-- 用法：mysql -u root -p < setup.sql
-- 前提：MySQL 8.0+ 已安装并启动
-- 注意：完整建表请以 docs/schema.sql 为准（含生成器 generators / 关联表 problem_generators /
--       比赛报名 contest_registrations 等全部表）；本脚本保留精简版，problems 表与 schema.sql 保持一致。

CREATE DATABASE IF NOT EXISTS oj CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
USE oj;

-- 用户表
CREATE TABLE IF NOT EXISTS users (
  id INT AUTO_INCREMENT PRIMARY KEY,
  username VARCHAR(64) NOT NULL UNIQUE,
  password_hash CHAR(64) NOT NULL,          -- SHA-256(salt + password)
  salt CHAR(32) NOT NULL,
  role ENUM('admin','author','user') NOT NULL DEFAULT 'user',
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- 会话表（登录态）
CREATE TABLE IF NOT EXISTS sessions (
  token CHAR(64) PRIMARY KEY,
  user_id INT NOT NULL,
  expires_at DATETIME NOT NULL,
  FOREIGN KEY (user_id) REFERENCES users(id)
);

-- 提交记录表（同一用户同一题重复提交 → 最后结果原地更新，不追加历史行）
CREATE TABLE IF NOT EXISTS submissions (
  id BIGINT AUTO_INCREMENT PRIMARY KEY,
  user_id INT NOT NULL,
  problem_id INT NOT NULL,
  contest_id INT NOT NULL DEFAULT 0,        -- 0 = 练习提交；>0 = 比赛 id
  verdict VARCHAR(16) NOT NULL,             -- AC / WA / TLE / RE / CE ...
  detail TEXT,
  code MEDIUMTEXT,                           -- 用户代码（AC 后锁定维护）
  time_ms INT,
  `virtual` TINYINT NOT NULL DEFAULT 0,       -- 1 = 虚拟参赛
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  UNIQUE KEY uq_user_problem (user_id, problem_id, contest_id),
  FOREIGN KEY (user_id) REFERENCES users(id)
);

-- 题目表（出题端「发布」时 upsert 上传；客户端启动时拉取）
CREATE TABLE IF NOT EXISTS problems (
  id          INT PRIMARY KEY,                  -- 题目编号（与题库目录名一致）
  title       VARCHAR(255) NOT NULL DEFAULT '',
  description MEDIUMTEXT,                       -- 题面描述
  sample_in   MEDIUMTEXT,                       -- 样例输入
  sample_out  MEDIUMTEXT,                       -- 样例输出
  time_ms     INT NOT NULL DEFAULT 1000,        -- 时间限制（毫秒）
  mem_mb      INT NOT NULL DEFAULT 256,         -- 内存限制（MB）
  tags        TEXT,                             -- JSON 数组原文，如 ["基础","模拟"]
  std_code    MEDIUMTEXT,                       -- 标准程序源码（AES-256 密文，base64）
  std_exe     LONGBLOB,                         -- 标准程序编译产物（二进制 exe，经接口 base64 传输）
  spj_code    MEDIUMTEXT,                       -- 特殊判题器源码（AES-256 密文，base64；空则不启用 SPJ）
  spj_exe     LONGBLOB,                         -- 特殊判题器编译产物（二进制 exe，经接口 base64 传输）
  entry_code  MEDIUMTEXT,                       -- 题目入口源码（函数式判题；AES-256 密文，base64）
  entry_exe   LONGBLOB,                         -- 题目入口编译产物（二进制 exe，经接口 base64 传输）
  itf_template MEDIUMTEXT,                      -- 接口模板（用户实现的函数骨架；AES-256 密文，base64）
  judge_mode  TINYINT NOT NULL DEFAULT 0,       -- 0=整程序判题 1=函数式判题（入口加载用户 DLL）
  is_public   TINYINT NOT NULL DEFAULT 1,       -- 1=公开（客户端可见）0=未公开（仅服务端可见）
  updated_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- 比赛表（contest.json 原文）
CREATE TABLE IF NOT EXISTS contests (
  id INT PRIMARY KEY,
  content MEDIUMTEXT NOT NULL
);

-- 匿名默认用户（客户端未登录的提交归到该账号；空哈希无法登录）
INSERT IGNORE INTO users(username, password_hash, salt, role)
VALUES ('anonymous', '', '', 'user');

-- 建一个初始管理员（密码 admin123，请务必登录后修改）
-- 若不想用 SQL 建管理员，可在客户端注册时把角色选为 admin（首个 admin 由 DBA 用 SQL 指定）
INSERT INTO users(username, password_hash, salt, role)
SELECT 'admin',
       SHA2('change_me_admin123', 256),
       'fixed_salt_change_me',
       'admin'
WHERE NOT EXISTS (SELECT 1 FROM users WHERE username='admin');
