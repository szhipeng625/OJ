-- OJ 数据库初始化脚本
-- 用法：mysql -u root -p < setup.sql
-- 前提：MySQL 8.0+ 已安装并启动

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
  time_ms INT,
  `virtual` TINYINT NOT NULL DEFAULT 0,       -- 1 = 虚拟参赛
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  UNIQUE KEY uq_user_problem (user_id, problem_id, contest_id),
  FOREIGN KEY (user_id) REFERENCES users(id)
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
