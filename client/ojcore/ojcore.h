// ojcore.h — OJ 核心引擎（C++ 动态链接库）导出接口（C ABI）
// 供 WPF 客户端通过 P/Invoke 直接调用，替代原来的 Go HTTP 服务。
#pragma once

#ifdef OJCORE_EXPORTS
#define OJ_API __declspec(dllexport)
#else
#define OJ_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

// 初始化引擎。problem_dir 题目根目录（每个子目录是一道题），
// data_dir 存储/临时数据目录。返回 0 成功，非 0 失败。
OJ_API int oj_init(const char* problem_dir, const char* data_dir);

// 获取题目列表，返回 JSON 字符串。调用方用完调 oj_free_string 释放。
// [{"id":1,"title":"A+B","description":"...","sampleIn":"...","sampleOut":"...",
//   "timeLimitMs":1000,"memLimitMB":256,"tags":["基础"],"version":3}]
OJ_API const char* oj_get_problems(void);

// 获取题目历史版本列表（读 {id}/history/* 每版题面+元数据），返回 JSON 字符串。
// [{"version":1,"title":"...","description":"...","timeLimitMs":1000,"memLimitMB":256,
//   "tags":[...],"updatedAt":"2026-09-19 12:00:00"}]
//
// 注意：题目历史版本仅服务端（author）可见，客户端不提供此接口，
//       发布到客户端题目目录时也不会携带 history 目录。

// 获取某道题的全部历史提交记录（从 LSM 存储扫描），返回 JSON 字符串。
// [{"id":1,"problemId":3,"verdict":"AC","detail":"...","timeMs":52,
//   "cases":[...],"ts":"2026-09-19 17:20:00"}]
OJ_API const char* oj_get_submissions(int problem_id);

// 提交判题。problem_id 题目编号，code 用户 C++ 源码。
// 返回 JSON 字符串，用完调 oj_free_string 释放。
// {"id":1,"verdict":"AC","detail":"...","cases":[{"name":"#1","timeMs":12,"passed":true,"info":""}]}
OJ_API const char* oj_submit(int problem_id, const char* code);

// ===== 比赛 / 榜单 =====

// 列出已发布的比赛（扫描 {problem_dir}/../contests/*/contest.json）。
// 返回 JSON：[{"id":1,"name":"...","problemCount":3,"startTime":"...","endTime":"..."}, ...]
OJ_API const char* oj_get_contests(void);

// 读取比赛详情。返回 contest.json 原文（带 ok 字段）：
// {"ok":true,"id":1,"name":"...","description":"...","startTime":"...","endTime":"...","problems":[1,2,3]}
OJ_API const char* oj_get_contest(int cid);

// 提交判题（比赛版）。username 参赛者昵称，virtual 是否虚拟参赛。
// submission 记录会带上 username / virtual，供榜单聚合。
OJ_API const char* oj_submit_ex(int problem_id, const char* code,
                                const char* username, int virtual_);

// 比赛榜单。按 problemId ∈ contest.problems 过滤提交，按 username 聚合。
// 每题 AC 时间（距 startTime 分钟）+ 每次错误提交 20 分钟罚时；按 AC 数降序、罚时升序。
// 返回 {"official":[{"rank":1,"username":"alice","solved":3,"penalty":125,"detail":{...}},...],
//       "virtual":[...]}
OJ_API const char* oj_get_board(int cid);

// 比赛提交（带 contest_id）、报名与比赛提交记录
OJ_API const char* oj_submit_contest(int problem_id, const char* code,
                                     const char* username, int virtual_,
                                     int contest_id);
OJ_API const char* oj_contest_register(int cid, const char* username, int virtual_);
OJ_API const char* oj_contest_registration(int cid, const char* username);
OJ_API const char* oj_contest_submissions(int cid);

// ===== MySQL 用户体系（可选，未配置时回退本地 anonymous）=====

// 初始化 MySQL 模式。host/port/user/pass/db 为连接参数；problem_dir 仍为题目目录。
// 若 libmysql.dll 未找到或连接失败，返回非 0 且仍可用本地模式。
OJ_API int oj_init_mysql(const char* host, int port, const char* user,
                         const char* pass, const char* db,
                         const char* problem_dir);

// 首次初始化建表（users/sessions/submissions）。返回 {"ok":true} 或 {"ok":false,"error":"..."}。
OJ_API const char* oj_mysql_init_schema(void);

// 注册。role: admin / author / user。返回 {"ok":true} 或 {"ok":false,"error":"..."}。
OJ_API const char* oj_register(const char* username, const char* password, const char* role);

// 登录。成功返回 {"ok":true,"token":"...","userId":1,"role":"admin","username":"..."}
OJ_API const char* oj_login(const char* username, const char* password);

// 校验 token。成功返回 {"ok":true,"userId":1,"role":"...","username":"..."}
OJ_API const char* oj_whoami(const char* token);

// 登出
OJ_API const char* oj_logout(const char* token);

// 列出全部用户（admin 用）。返回 JSON 数组。
OJ_API const char* oj_list_users(const char* token);

// ===== 题目数据生成器（LSM 存历史版本，MySQL 存最新版本，重复保存原地替换） =====

// 保存生成器代码：自动分配新版本号，写入 LSM 历史版本，同时 upsert 到 MySQL 最新版本。
// 返回 {"ok":true,"version":N}；MySQL 未连接时仅写 LSM，仍返回 ok。
OJ_API const char* oj_gen_save(int problem_id, const char* code);

// 读取某题最新生成器代码（优先 MySQL，回退 LSM 最新版本）。
// 返回 {"ok":true,"code":"...","version":N,"updatedAt":"..."}；无记录返回 {"ok":false}。
OJ_API const char* oj_gen_get_current(int problem_id);

// 列出某题的全部历史版本（从 LSM 扫描），按版本号降序。
// 返回 [{"version":1,"ts":"2026-09-19 12:00:00","lines":18,"summary":"#include..."}, ...]
OJ_API const char* oj_gen_list_versions(int problem_id);

// 读取某题指定历史版本的代码（从 LSM）。
// 返回 {"ok":true,"code":"...","version":N,"ts":"..."}；不存在返回 {"ok":false}。
OJ_API const char* oj_gen_get_version(int problem_id, int version);

// 跨题查找生成器代码（遍历 MySQL 全部题目的最新代码，大小写不敏感）。
// 返回 [{"problemId":111,"version":3,"updatedAt":"...","preview":"前3行..."}, ...]
OJ_API const char* oj_gen_search(const char* keyword);

// 释放 oj_get_problems / oj_submit 返回的字符串。
OJ_API void oj_free_string(const char* s);

#ifdef __cplusplus
}
#endif
