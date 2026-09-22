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

// 连接远端 LSM 存储服务（RESP，Redis 协议，默认端口 6379），用于提交/报名等持久化。
// 返回 0 成功，非 0 失败（失败时判题仍可用，只是提交记录不持久化）。
OJ_API int oj_init_redis(const char* host, int port);

// 获取题目列表，返回 JSON 字符串。调用方用完调 oj_free_string 释放。
// [{"id":1,"title":"A+B","description":"...","sampleIn":"...","sampleOut":"...",
//   "timeLimitMs":1000,"memLimitMB":256,"tags":["基础"]}]
OJ_API const char* oj_get_problems(void);

// 获取某道题的全部历史提交记录（从远端 LSM 存储读取），返回 JSON 字符串。
// [{"id":1,"problemId":3,"verdict":"AC","detail":"...","timeMs":52,
//   "cases":[...],"ts":"2026-09-19 17:20:00"}]
OJ_API const char* oj_get_submissions(int problem_id);

// 获取某用户在某题（contest_id=0 为练习）下的最近一次提交（含 code）。
// 无记录返回 {"found":false}；有则 {"found":true,"id":..,"verdict":"..","code":"..",...}
OJ_API const char* oj_get_user_solution(int problem_id, const char* username, int contest_id);

// 某用户练习模式（contest_id=0）每题最近一次提交结果。
// 返回 [{"problemId":1,"verdict":"AC","ac":true}, ...]
OJ_API const char* oj_get_user_progress(const char* username);

// 某用户在某场比赛下每题最近一次提交结果（同上格式）。
OJ_API const char* oj_get_user_contest_progress(int cid, const char* username);

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
// view_all=0 时只返回 username 本人记录（比赛进行中参赛者互不可见）
OJ_API const char* oj_contest_submissions(int cid, const char* username, int view_all);

// ===== MySQL 用户体系（可选，未配置时回退本地 anonymous）=====

// 初始化 MySQL 模式。host/port/user/pass/db 为连接参数；problem_dir 仍为题目目录；
// data_dir 为本地数据目录（提交记录/临时文件，相对 exe 目录传入）。
// 若 libmysql.dll 未找到或连接失败，返回非 0 且仍可用本地模式。
OJ_API int oj_init_mysql(const char* host, int port, const char* user,
                         const char* pass, const char* db,
                         const char* problem_dir, const char* data_dir);

// 首次初始化建表（users/sessions/submissions）。返回 {"ok":true} 或 {"ok":false,"error":"..."}。
OJ_API const char* oj_mysql_init_schema(void);

// 注册。role: admin / author / user。返回 {"ok":true} 或 {"ok":false,"error":"..."}。
OJ_API const char* oj_register(const char* username, const char* password, const char* role);

// 登录。成功返回 {"ok":true,"token":"...","userId":1,"role":"admin","username":"..."}
OJ_API const char* oj_login(const char* username, const char* password);

// 校验 token。成功返回 {"ok":true,"userId":1,"role":"...","username":"...","nickname":"...","avatar":"..."}
OJ_API const char* oj_whoami(const char* token);

// 更新当前用户资料（昵称 / 头像，头像为 data URL 或空串）。返回 {"ok":true} 或 {"ok":false,"error":"..."}
OJ_API const char* oj_update_profile(const char* token, const char* nickname, const char* avatar);

// 登出
OJ_API const char* oj_logout(const char* token);

// 列出全部用户（admin 用）。返回 JSON 数组。
OJ_API const char* oj_list_users(const char* token);

// ===== 题目 / 比赛发布与同步（MySQL 分发） =====

// 发布题目到 MySQL：读取 problem_dir 目录下的题面/元数据/测试数据文件，upsert 到 problems/problem_files。
// 返回 {"ok":true} 或 {"ok":false,"error":"..."}。
OJ_API const char* oj_mysql_publish_problem(int id, const char* problem_dir);

// 发布比赛到 MySQL：contest_json 为比赛 JSON（含 name/description/startTime/endTime/problems）。
// 返回 {"ok":true} 或 {"ok":false,"error":"..."}。
OJ_API const char* oj_mysql_publish_contest(int cid, const char* contest_json);

// 从 MySQL 同步题目与比赛到本地目录（g_problemDir 与 serverRoot）。
// 返回 {"ok":true} 或 {"ok":false,"error":"..."}。
OJ_API const char* oj_mysql_sync_problems(void);

// 释放 oj_get_problems / oj_submit 返回的字符串。
OJ_API void oj_free_string(const char* s);

#ifdef __cplusplus
}
#endif
