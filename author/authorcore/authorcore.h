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

// 编译源码（任意 .cpp 源），返回 {"ok":true} 或 {"ok":false,"error":"..."}
AC_API const char* ac_compile(const char* src_file, const char* exe_file);

// 运行标程对题目所有 *.in 生成 *.out（每个限时 2s / 内存 256MB）
// 返回 {"ok":true,"results":[{"file":"1.in","status":"OK|TLE|RE|SE","ms":10}, ...]}
AC_API const char* ac_gen_outputs(int id, const char* std_exe);

// 完整性校验，返回 {"ok":bool,"inCount":n,"genCount":m,"hasStd":bool,"missing":[...]}
AC_API const char* ac_validate(int id);

// 分发：复制 {id} 到 target_root\{id}（history / gen_history 等历史目录不下发）。
// 返回 {"ok":true,"target":"..."}
AC_API const char* ac_publish(int id, const char* target_root);

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
// ===== 生成器（数据生成器，全部由 C++ 端负责存储，前端只展示） =====
// 多生成器模型：每道题下有 N 个生成器，各自独立目录 {题根}/{id}/{genName}/
//    gen.cpp   生成器源码
//    desc.txt  自定义描述（如“菊花图生成器”）
//    生成的 *.in/*.out 直接落在此目录（即判题数据源）

// 设置生成器临时目录（编译生成 exe 用）
AC_API void ac_gen_set_temp(const char* temp_dir);

// 列出题目下所有数据生成器，[{"name":"juhua","desc":"菊花图生成器","fileCount":N}]
AC_API const char* ac_gen_list(int id);

// 新建一个数据生成器，{"ok":true} / {"ok":false,"error":"..."}
AC_API const char* ac_gen_create(int id, const char* name, const char* desc);

// 获取指定生成器的当前代码 + 描述 + 路径（无 gen.cpp 则返回模板）
// {"ok":true,"name":"...","desc":"...","code":"...","genPath":"...","outDir":"..."}
AC_API const char* ac_gen_get_current(int id, const char* name);

// 修改生成器描述，{"ok":true}
AC_API const char* ac_gen_set_desc(int id, const char* name, const char* desc);

// 保存生成器代码（直接覆盖 gen.cpp，不再保留历史版本）
// {"ok":true}
AC_API const char* ac_gen_save(int id, const char* name, const char* code);

// 编译指定生成器为 exe（temp\\{id}\\{name}.exe），{"ok":true} / {"ok":false,"error":"..."}
AC_API const char* ac_gen_compile(int id, const char* name);

// 运行指定生成器：每个测试点独立运行一次，stdout 重定向写入该生成器子目录的 1.in~n.in
// （argv[1]=输出目录, argv[2]=种子, argv[3]=总组数 n, argv[4]=当前组号 i，生成器直接 cout 即可）
// {"ok":true,"generated":N,"total":N,"outDir":"...","fails":[...]}
AC_API const char* ac_gen_run(int id, const char* name, int n);

// 指定生成器生成的数据文件列表，[{"name":"1.in","size":165,"modified":"09-19 20:09"}]
AC_API const char* ac_gen_list_files(int id, const char* name);

// 获取某个生成的数据文件内容（限 200KB），{"ok":true,"content":"...","truncated":bool}
AC_API const char* ac_gen_get_file(int id, const char* name, const char* file);

// 跨题目搜索生成器代码（大小写不敏感，每题目最多 30 个命中）
// [{"pid":111,"title":"...","lineNo":5,"line":"...","keyword":"..."}]
AC_API const char* ac_gen_search(const char* keyword);

// 把某生成器生成的 .in 文件导入到题目根目录，{"ok":true}
AC_API const char* ac_gen_import_to_problem(int id, const char* name, const char* filename);

// ===== 数据生成器本地测试（DB 代码 → 临时目录编译运行，不触碰题库目录） =====
// id = 生成器 id（仅作临时目录命名空间），name = 生成器名字
AC_API const char* ac_gen_test_compile(int id, const char* name, const char* code);
AC_API const char* ac_gen_test_run(int id, const char* name, int n);
AC_API const char* ac_gen_test_files(int id, const char* name);
AC_API const char* ac_gen_test_file(int id, const char* name, const char* file);

// 读取本题勾选使用的生成器（返回 JSON 数组，如 ["juhua","lian"]；只保留仍然存在的名字）
AC_API const char* ac_gen_get_used(int id);

// 保存本题勾选使用的生成器（names_json 为 JSON 数组，如 ["juhua","lian"]，覆盖写 gen_used.txt）
AC_API const char* ac_gen_set_used(int id, const char* names_json);

}
