#pragma once
// authorcore.h — 出题服务端核心 DLL 导出声明

#ifdef AUTHORCORE_EXPORTS
#define AC_API __declspec(dllexport)
#else
#define AC_API __declspec(dllimport)
#endif

extern "C" {

// 设置题库根目录（如 D:\OJ\author\problems）
AC_API int ac_init(const char* problems_dir);

// 返回题目列表 JSON：[{"id":1,"title":"...","dataCount":3}, ...]
AC_API const char* ac_list(void);

// 创建题目（id 已存在则失败），返回 {"ok":true} 或 {"ok":false,"error":"..."}
AC_API const char* ac_create(int id, const char* title, const char* desc,
                             const char* sample_in, const char* sample_out);

// 更新题面与样例
AC_API const char* ac_save_statement(int id, const char* title, const char* desc,
                                     const char* sample_in, const char* sample_out);

// 保存题目元数据：时间限制(ms)、内存限制(MB)、标签（JSON 数组，如 ["基础","模拟"]）
AC_API const char* ac_save_meta(int id, int time_ms, int mem_mb, const char* tags_json);

// 读取题目元数据，返回 meta.json 内容（JSON 对象）
AC_API const char* ac_get_meta(int id);

// 编译源码（标程 std.cpp 或 spj.cpp），返回 {"ok":true} 或 {"ok":false,"error":"..."}
AC_API const char* ac_compile(const char* src_file, const char* exe_file);

// 运行标程对题目所有 *.in 生成 *.out（每个限时 2s / 内存 256MB）
// 返回 {"ok":true,"results":[{"file":"1.in","status":"OK|TLE|RE|SE","ms":10}, ...]}
AC_API const char* ac_gen_outputs(int id, const char* std_exe);

// 完整性校验，返回 {"ok":bool,"inCount":n,"hasStd":bool,"hasSpj":bool,"missing":[...]}
AC_API const char* ac_validate(int id);

// 分发：先快照到题库 {id}\history\{v}（版本自增），再复制 {id} 到 target_root\{id}。
// 注意：history 目录仅服务端可见，发布到客户端时会被跳过。
// 返回 {"ok":true,"target":"...","version":N}
AC_API const char* ac_publish(int id, const char* target_root);

// 题目历史版本列表，返回 JSON：
// [{"version":1,"title":"...","timeLimitMs":1000,"memLimitMB":256,"tags":[...],"updatedAt":"..."}]
AC_API const char* ac_get_history(int id);

// ===== 比赛管理 =====

// 创建/更新比赛。problems_json 为题目 id 数组，如 "[1,2,3]"。
// 配置写入 {problems_dir}\contests\{cid}\contest.json
AC_API const char* ac_contest_create(int cid, const char* name, const char* desc,
                                     const char* start_time, const char* end_time,
                                     const char* problems_json);

// 比赛列表，返回 JSON：[{"id":1,"name":"...","problemCount":3,"startTime":"...","endTime":"..."}]
AC_API const char* ac_contest_list(void);

// 比赛详情，返回 {"ok":true,"id":1,"name":"...","description":"...","startTime":"...","endTime":"...","problems":[1,2,3]}
AC_API const char* ac_contest_get(int cid);

// 发布比赛：复制 contests\{cid} 到 target_root\contests\{cid}
AC_API const char* ac_contest_publish(int cid, const char* target_root);

// 最近一次错误信息
AC_API const char* ac_last_error(void);

// 释放返回字符串
AC_API void ac_free_string(const char* s);

// ===== 数据生成器管理（存储全部在 C++ 端，前端只展示） =====

// 设置临时目录（存放编译后的生成器 exe）
AC_API void ac_gen_set_temp(const char* temp_dir);

// 读取当前生成器代码（不存在则返回模板），同时返回路径
// {"ok":true,"hasGen":bool,"code":"...","genPath":"...","outDir":"..."}
AC_API const char* ac_gen_get_current(int id);

// 保存生成器代码并归档版本快照 vN_yyyyMMdd_HHmmss.cpp
// {"ok":true,"version":N,"snapshot":"vN_....cpp"}
AC_API const char* ac_gen_save(int id, const char* code);

// 版本列表（新版在前）：[{"version":1,"stamp":"...","fileName":"...","lines":N,"summary":"...","time":"..."}]
AC_API const char* ac_gen_list_versions(int id);

// 读取某个版本的代码内容：{"ok":true,"code":"..."}
AC_API const char* ac_gen_get_version(int id, int version);

// 编译已保存的生成器 → temp\{id}\{id}.exe：{"ok":true} / {"ok":false,"error":"..."}
AC_API const char* ac_gen_compile(int id);

// 运行生成器产出 count 个数据文件（编号从 start 开始），每个限时 20s
// {"ok":true,"generated":N,"total":M,"outDir":"...","fails":["#i 原因"]}
AC_API const char* ac_gen_run(int id);

// 生成的数据文件列表：[{"name":"100.in","size":165,"modified":"09-19 20:09"}]
AC_API const char* ac_gen_list_files(int id);

// 读取某个生成的数据文件内容（限 200KB）：{"ok":true,"content":"...","truncated":bool}
AC_API const char* ac_gen_get_file(int id, const char* name);

// 跨题查找生成器代码（大小写不敏感，每题最多 30 条）
// [{"pid":111,"title":"...","lineNo":5,"line":"...","keyword":"..."}]
AC_API const char* ac_gen_search(const char* keyword);

// 把生成的 .in 文件导入到题目测试数据目录：{"ok":true}
AC_API const char* ac_gen_import_to_problem(int id, const char* name);

}
