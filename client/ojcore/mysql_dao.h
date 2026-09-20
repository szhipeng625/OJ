// mysql_dao.h — MySQL 持久化层（动态加载 libmysql.dll，不硬链接）
// 设计：运行时 LoadLibrary("libmysql.dll")；未安装 MySQL Connector/C 时
//       ojcore 自动回退本地 tiny-lsm 模式，现有功能不受影响。
#pragma once
#include <string>

namespace oj {

// libmysql.dll 是否加载成功
bool mysql_available();

// 连接数据库。失败返回 false 并填 err。
bool mysql_connect(const std::string& host, unsigned int port,
                   const std::string& user, const std::string& pass,
                   const std::string& db, std::string& err);

void mysql_disconnect();

// 建表（首次初始化调用，IF NOT EXISTS）
bool mysql_init_schema(std::string& err);

// 注册。role: "admin" / "author" / "user"。成功返回 true。
bool mysql_register(const std::string& username, const std::string& password,
                    const std::string& role, std::string& err);

// 登录。成功返回 true，out_token/out_user_id/out_role/out_username 填充。
bool mysql_login(const std::string& username, const std::string& password,
                 std::string& out_token, long long& out_user_id,
                 std::string& out_role, std::string& out_username, std::string& err);

// token 会话校验
bool mysql_whoami(const std::string& token, long long& out_user_id,
                  std::string& out_role, std::string& out_username, std::string& err);

bool mysql_logout(const std::string& token);

// 写提交记录：唯一键 (user_id, problem_id, contest_id)，
// 同一用户同一题重复提交时原地更新最后结果（upsert，不追加历史行）
bool mysql_upsert_submission(long long user_id, int problem_id, int contest_id,
                             const std::string& verdict, const std::string& detail,
                             int time_ms, bool virt, const std::string& ts);

// 按用户名查用户 id（存在返回 true 并填 out_user_id）
bool mysql_user_id_by_name(const std::string& username, long long& out_user_id);

// 榜单聚合：official / virtual 两个 JSON 数组（ICPC 规则，20 分钟罚时）
bool mysql_board(int cid, const std::string& start_str,
                 const std::string& problems_csv,
                 std::string& out_official_json, std::string& out_virtual_json);

// admin：列出全部用户（返回 JSON 数组）
bool mysql_list_users(std::string& out_json, std::string& err);

// ===== 题目数据生成器（最新版本存 MySQL，历史版本存 LSM） =====

// 保存/更新某题的最新生成器代码（problem_id 主键，重复则原地替换）
bool mysql_gen_upsert(int problem_id, const std::string& code, int version,
                       const std::string& updated_at, std::string& err);

// 读取某题的最新生成器代码；不存在返回 false
bool mysql_gen_get(int problem_id, std::string& out_code, int& out_version,
                    std::string& out_updated_at, std::string& err);

// 跨题查找生成器代码（遍历全部题目的最新代码，大小写不敏感，每题最多 30 行）
bool mysql_gen_search(const std::string& keyword, std::string& out_json, std::string& err);

} // namespace oj
