// mysql_dao.h — 数据访问层（统一经中间层 HTTP 网关，不再直连 MySQL）
#pragma once
#include <string>

namespace oj {

// 中间层已配置即视为可用
bool mysql_available();

// 中间层 HTTP 模式状态
void middleware_set_url(const std::string& url);
const std::string& middleware_url();
void middleware_set_token(const std::string& token);
const std::string& middleware_token();

// 连接（兼容保留的空操作；中间层模式下不再直连，返回 false）
bool mysql_connect(const std::string& host, unsigned int port,
                   const std::string& user, const std::string& pass,
                   const std::string& db, std::string& err);

// 建表（schema 由中间层管理，客户端不再建表，恒返回 true）
bool mysql_init_schema(std::string& err);

// 注册 / 登录 / 会话
// 注册只允许 user 角色；email/name/school 为用户资料（可空）。
bool mysql_register(const std::string& username, const std::string& password,
                    const std::string& email, const std::string& name,
                    const std::string& school, std::string& err);
bool mysql_login(const std::string& username, const std::string& password,
                 std::string& out_token, long long& out_user_id,
                 std::string& out_role, std::string& out_username,
                 std::string& out_nickname, std::string& out_avatar,
                 std::string& out_email, std::string& out_name,
                 std::string& out_school, std::string& err);
bool mysql_whoami(const std::string& token, long long& out_user_id,
                  std::string& out_role, std::string& out_username,
                  std::string& out_nickname, std::string& out_avatar,
                  std::string& out_email, std::string& out_name,
                  std::string& out_school, std::string& err);
bool mysql_logout(const std::string& token);
bool mysql_update_profile(long long user_id, const std::string& nickname,
                          const std::string& avatar, std::string& err);

// 提交 / 用户进度 / 榜单
long long mysql_upsert_submission(int problem_id, int contest_id,
                                  const std::string& verdict, const std::string& detail,
                                  int time_ms, bool virt, const std::string& ts,
                                  const std::string& code);
bool mysql_user_id_by_name(const std::string& username, long long& out_user_id);
bool mysql_user_progress(const std::string& username, int contest_id, std::string& out_json);
bool mysql_user_solution(int problem_id, int contest_id, std::string& out_json);
bool mysql_contest_register(long long user_id, int contest_id, bool virt, std::string& err);
bool mysql_contest_registration(long long user_id, int contest_id,
                                bool& registered, bool& virt);
bool mysql_contest_submissions(int cid, const std::string& username, bool view_all,
                               std::string& out_json);
bool mysql_board(int cid, const std::string& start_str,
                 const std::string& problems_csv,
                 std::string& out_official_json, std::string& out_virtual_json);
bool mysql_list_users(std::string& out_json, std::string& err);

// 题目 / 比赛
bool mysql_upsert_problem(int id, const std::string& title, const std::string& desc,
                          const std::string& sample_in, const std::string& sample_out,
                          int time_ms, int mem_mb, const std::string& tags_json,
                          const std::string& std_code, bool is_public, std::string& err);
bool mysql_problem_visibility(std::string& out_json);
bool mysql_list_problems(std::string& out_json);
bool mysql_get_problem(int id, std::string& out_json);
bool mysql_upsert_contest(int cid, const std::string& contest_json, std::string& err);
bool mysql_list_contests(std::string& out_json);
bool mysql_get_contest(int cid, std::string& out_json);
bool mysql_sync_problems(const std::string& problem_dir, const std::string& server_root, std::string& err);
// 判题前按需生成数据（初始化不再预生成）；生成耗时不计入判题计时。
bool ensure_problem_data(int problem_id, const std::string& problem_dir, std::string& err);

// 生成器
bool mysql_list_generators(std::string& out_json);
bool mysql_get_generator(int id, std::string& out_name, std::string& out_code, std::string& out_desc);
bool mysql_create_generator(const std::string& name, const std::string& code, const std::string& desc,
                            long long& out_id, std::string& err);
bool mysql_update_generator(int id, const std::string& code, const std::string& desc, std::string& err);
bool mysql_delete_generator(int id);
bool mysql_problem_generators(int problem_id, std::string& out_json);
bool mysql_bind_generator(int problem_id, int generator_id, int gen_count, std::string& err);
bool mysql_unbind_generator(int problem_id, int generator_id);
bool mysql_search_generators(const std::string& keyword, std::string& out_json);

// 测试样例
bool mysql_set_testcase(int problem_id, const std::string& name,
                        const std::string& input, const std::string& output, bool is_sample);
bool mysql_remove_testcase(int problem_id, const std::string& name);
bool mysql_list_testcases(int problem_id, std::string& out_json);

} // namespace oj
