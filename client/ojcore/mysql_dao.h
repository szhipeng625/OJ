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

// 登录。成功返回 true，out_token/out_user_id/out_role/out_username/out_nickname/out_avatar 填充。
bool mysql_login(const std::string& username, const std::string& password,
                 std::string& out_token, long long& out_user_id,
                 std::string& out_role, std::string& out_username,
                 std::string& out_nickname, std::string& out_avatar, std::string& err);

// token 会话校验
bool mysql_whoami(const std::string& token, long long& out_user_id,
                  std::string& out_role, std::string& out_username,
                  std::string& out_nickname, std::string& out_avatar, std::string& err);

bool mysql_logout(const std::string& token);

// 更新用户资料（昵称 / 头像，头像为 data URL 或空串）
bool mysql_update_profile(long long user_id, const std::string& nickname,
                          const std::string& avatar, std::string& err);

// 写提交记录：唯一键 (user_id, problem_id, contest_id)，
// 同一用户同一题重复提交时原地更新最后结果（upsert，不追加历史行）
bool mysql_upsert_submission(long long user_id, int problem_id, int contest_id,
                             const std::string& verdict, const std::string& detail,
                             int time_ms, bool virt, const std::string& ts);

// 按用户名查用户 id（存在返回 true 并填 out_user_id）
bool mysql_user_id_by_name(const std::string& username, long long& out_user_id);

// 比赛报名（幂等：重复报名更新虚拟标记）
bool mysql_contest_register(long long user_id, int contest_id, bool virt, std::string& err);

// 查询某用户的报名状态：registered=是否已报名，virt=是否虚拟参赛
bool mysql_contest_registration(long long user_id, int contest_id,
                                bool& registered, bool& virt);

// 比赛提交记录 JSON 数组（时间倒序；每人每题保留最后一次结果）
// view_all=false 时只返回 username 本人记录（比赛进行中参赛者互不可见）
bool mysql_contest_submissions(int cid, const std::string& username, bool view_all,
                               std::string& out_json);

// 榜单聚合：official / virtual 两个 JSON 数组（ICPC 规则，20 分钟罚时）
bool mysql_board(int cid, const std::string& start_str,
                 const std::string& problems_csv,
                 std::string& out_official_json, std::string& out_virtual_json);

// admin：列出全部用户（返回 JSON 数组）
bool mysql_list_users(std::string& out_json, std::string& err);

// ===== 题目 / 比赛发布与同步（服务端上传、客户端拉取） =====

// 加密 / 解密（生成器源码与标程在库中以密文存储，客户端本地解密后使用）
std::string encrypt_blob(const std::string& plain);
std::string decrypt_blob(const std::string& enc);

// upsert 一道题的题面、元数据与标程源码（std_code 明文传入，内部加密存储）
bool mysql_upsert_problem(int id, const std::string& title, const std::string& desc,
                          const std::string& sample_in, const std::string& sample_out,
                          int time_ms, int mem_mb, const std::string& tags_json,
                          const std::string& std_code, std::string& err);

// 清空某题与生成器的全部关联（重新发布前调用，旧关联指向的生成器随后被清理）
bool mysql_clear_problem_generators(int problem_id);

// 新增一个生成器：code 明文传入，内部加密后写入 generators 并关联到题目。
// 返回新生成器 id（>0 成功，0 失败）。
long long mysql_add_problem_generator(int problem_id, const std::string& name,
                                      const std::string& code, const std::string& desc,
                                      int gen_count, int seed_base);

// 清理不再被任何题目引用的孤儿生成器（发布后调用）
void mysql_purge_orphan_generators();

// upsert 一场比赛的 contest.json 原文
bool mysql_upsert_contest(int cid, const std::string& contest_json, std::string& err);

// 把 MySQL 中的题目与比赛物化到本地目录（problem_dir 下按 id 建目录，server_root 下建 contests）
bool mysql_sync_problems(const std::string& problem_dir, const std::string& server_root, std::string& err);

} // namespace oj
