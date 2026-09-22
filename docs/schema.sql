-- ============================================================
-- OJ 数据库完整建表脚本（服务端 + 客户端共用）
-- 与 ojcore.dll 的 mysql_init_schema 自动建表保持一致（幂等）
--
-- 用法：mysql -u root -p < schema.sql
-- 前提：MySQL 8.0+（客户端运行时还会自动 CREATE DATABASE oj）
--
-- 说明：
--   ① users                用户账号（密码 SHA-256(salt + password) 存储）
--   ② sessions             登录会话（token，7 天有效）
--   ③ submissions          提交记录（同一用户同一题最后一发原地更新）
--   ④ contest_registrations 比赛报名（正式 / 虚拟参赛）
--   ⑤ problems             题目（题面 + 元数据 + 标程源码，出题端发布上传）
--   ⑥ generators           生成器（独立表，源码密文存储）
--   ⑥' problem_generators   题目 ↔ 生成器关联（多对多，带组数/种子/目录名）
--   ⑦ contests             比赛配置（contest.json 原文）
-- ============================================================

CREATE DATABASE IF NOT EXISTS oj CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
USE oj;

-- ------------------------------------------------------------
-- ① 用户表
-- ------------------------------------------------------------
CREATE TABLE IF NOT EXISTS users (
  id            INT AUTO_INCREMENT PRIMARY KEY,
  username      VARCHAR(64)  NOT NULL UNIQUE,
  password_hash CHAR(64)     NOT NULL,          -- SHA-256(salt + password)
  salt          CHAR(32)     NOT NULL,          -- 每用户随机 16 字节 → 32 位十六进制
  role          ENUM('admin','author','user') NOT NULL DEFAULT 'user',
  nickname      VARCHAR(64)  NOT NULL DEFAULT '',
  avatar        MEDIUMTEXT,                     -- base64 data URL
  created_at    DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ------------------------------------------------------------
-- ② 会话表（登录态）
-- ------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sessions (
  token      CHAR(64) PRIMARY KEY,              -- 32 字节随机 → 64 位十六进制
  user_id    INT NOT NULL,
  expires_at DATETIME NOT NULL,                 -- 登录后 7 天
  FOREIGN KEY (user_id) REFERENCES users(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ------------------------------------------------------------
-- ③ 提交记录表
-- 唯一键 (user_id, problem_id, contest_id)：同一用户对同一题重复提交
-- 由应用 INSERT ... ON DUPLICATE KEY UPDATE 原地覆盖为最后结果，
-- 每人每题只保留一条最新记录，不追加历史行。
-- contest_id = 0 表示练习提交。
-- ------------------------------------------------------------
CREATE TABLE IF NOT EXISTS submissions (
  id         BIGINT AUTO_INCREMENT PRIMARY KEY,
  user_id    INT NOT NULL,
  problem_id INT NOT NULL,
  contest_id INT NOT NULL DEFAULT 0,
  verdict    VARCHAR(16) NOT NULL,              -- AC / WA / TLE / RE / CE / SE
  detail     TEXT,
  time_ms    INT,
  `virtual`  TINYINT NOT NULL DEFAULT 0,        -- 1 = 虚拟参赛
  wrong_count INT NOT NULL DEFAULT 0,           -- AC 前错误次数（ICPC 罚时 +20 分钟/次）
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  UNIQUE KEY uq_user_problem (user_id, problem_id, contest_id),
  KEY idx_contest_problem (contest_id, problem_id),   -- 榜单 / 比赛提交查询
  FOREIGN KEY (user_id) REFERENCES users(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ------------------------------------------------------------
-- ④ 比赛报名表（幂等：重复报名更新虚拟标记）
-- ------------------------------------------------------------
CREATE TABLE IF NOT EXISTS contest_registrations (
  id            INT AUTO_INCREMENT PRIMARY KEY,
  user_id       INT NOT NULL,
  contest_id    INT NOT NULL,
  is_virtual    TINYINT NOT NULL DEFAULT 0,
  registered_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  UNIQUE KEY uq_user_contest (user_id, contest_id),
  FOREIGN KEY (user_id) REFERENCES users(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ------------------------------------------------------------
-- ⑤ 题目表（出题端「发布」时 upsert 上传；客户端启动时拉取）
-- ------------------------------------------------------------
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
  is_public   TINYINT NOT NULL DEFAULT 1,       -- 1=公开（客户端可见）0=未公开（仅服务端可见）
  updated_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ------------------------------------------------------------
-- ⑥ 生成器表（全局库，所有数据生成器放一张表）
-- 只存生成器源码（密文）+ 名称 + 描述，不存生成的 .in/.out；客户端拉取后本地解密、编译、运行重新生成判题数据。
-- 一个生成器可被多道题复用；题目通过 problem_generators 关联到这里的 id。
-- ------------------------------------------------------------
CREATE TABLE IF NOT EXISTS generators (
  id          INT AUTO_INCREMENT PRIMARY KEY,
  name        VARCHAR(64) NOT NULL,            -- 生成器全局唯一名（如 juhua）
  code        MEDIUMTEXT NOT NULL,             -- gen.cpp 源码（AES-256 密文，base64）
  description TEXT,                             -- 描述（明文）
  created_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  UNIQUE KEY uq_generator_name (name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ------------------------------------------------------------
-- ⑥' 题目 ↔ 生成器关联表（多对多）
-- gen_count = 测试点数量 n；seed_base = 确定性随机种子基准（所有客户端生成同一份数据）。
-- ------------------------------------------------------------
CREATE TABLE IF NOT EXISTS problem_generators (
  problem_id   INT NOT NULL,
  generator_id INT NOT NULL,
  gen_count    INT NOT NULL DEFAULT 0,
  seed_base    INT NOT NULL DEFAULT 0,
  PRIMARY KEY (problem_id, generator_id),
  FOREIGN KEY (problem_id) REFERENCES problems(id) ON DELETE CASCADE,
  FOREIGN KEY (generator_id) REFERENCES generators(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ------------------------------------------------------------
-- ⑦ 比赛表（contest.json 原文：id/name/description/startTime/endTime/problems）
-- ------------------------------------------------------------
CREATE TABLE IF NOT EXISTS contests (
  id      INT PRIMARY KEY,                      -- 比赛编号
  content MEDIUMTEXT NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- ------------------------------------------------------------
-- 种子数据
-- ------------------------------------------------------------

-- 匿名默认用户：客户端未登录的提交归到该账号（空哈希无法登录）
INSERT IGNORE INTO users(username, password_hash, salt, role, nickname)
VALUES ('anonymous', '', '', 'user', 'anonymous');

-- 初始管理员（密码 admin123，请登录后修改）
-- 哈希算法与 App 一致：SHA-256(salt + password)
SET @seed_salt = '00000000000000000000000000000001';
INSERT INTO users(username, password_hash, salt, role, nickname)
SELECT 'admin',
       SHA2(CONCAT(@seed_salt, 'admin123'), 256),
       @seed_salt,
       'admin',
       'admin'
WHERE NOT EXISTS (SELECT 1 FROM users WHERE username = 'admin');
