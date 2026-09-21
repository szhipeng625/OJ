// mysql_dao.cpp — 动态加载 libmysql.dll，封装用户/会话/提交/榜单
#include "mysql_dao.h"

#include <windows.h>
#include <bcrypt.h>
#include <wincrypt.h>

#include <algorithm>
#include <map>
#include <string>
#include <vector>

#include <cstdio>
#include <cstring>
#include <ctime>
#pragma comment(lib, "bcrypt.lib")

namespace {

// ---------- libmysql 函数指针 ----------
typedef void*  (*PFN_mysql_init)(void*);
typedef void*  (*PFN_mysql_real_connect)(void*, const char*, const char*, const char*,
                                         const char*, unsigned int, const char*, unsigned long);
typedef int    (*PFN_mysql_query)(void*, const char*);
typedef void*  (*PFN_mysql_store_result)(void*);
typedef char** (*PFN_mysql_fetch_row)(void*);
typedef void   (*PFN_mysql_free_result)(void*);
typedef void   (*PFN_mysql_close)(void*);
typedef const char* (*PFN_mysql_error)(void*);
typedef unsigned long long (*PFN_mysql_insert_id)(void*);
typedef unsigned long (*PFN_mysql_real_escape_string)(void*, char*, const char*, unsigned long);
typedef unsigned int (*PFN_mysql_errno)(void*);
typedef unsigned int (*PFN_mysql_field_count)(void*);

struct MySqlApi {
    HMODULE dll = nullptr;
    PFN_mysql_init            mysql_init = nullptr;
    PFN_mysql_real_connect    mysql_real_connect = nullptr;
    PFN_mysql_query           mysql_query = nullptr;
    PFN_mysql_store_result    mysql_store_result = nullptr;
    PFN_mysql_fetch_row       mysql_fetch_row = nullptr;
    PFN_mysql_free_result     mysql_free_result = nullptr;
    PFN_mysql_close           mysql_close = nullptr;
    PFN_mysql_error           mysql_error = nullptr;
    PFN_mysql_insert_id       mysql_insert_id = nullptr;
    PFN_mysql_real_escape_string mysql_real_escape_string = nullptr;
    PFN_mysql_errno           mysql_errno = nullptr;
    PFN_mysql_field_count     mysql_field_count = nullptr;
};

MySqlApi g_sql;
void*    g_conn = nullptr;
bool     g_loaded = false;

#define LOAD_FN(name) g_sql.name = (PFN_##name)GetProcAddress(g_sql.dll, #name); \
                      if (!g_sql.name) return false;

bool LoadMySql() {
    if (g_loaded) return g_sql.dll != nullptr;
    g_loaded = true;
    g_sql.dll = LoadLibraryA("libmysql.dll");
    if (!g_sql.dll) return false;
    LOAD_FN(mysql_init);
    LOAD_FN(mysql_real_connect);
    LOAD_FN(mysql_query);
    LOAD_FN(mysql_store_result);
    LOAD_FN(mysql_fetch_row);
    LOAD_FN(mysql_free_result);
    LOAD_FN(mysql_close);
    LOAD_FN(mysql_error);
    LOAD_FN(mysql_insert_id);
    LOAD_FN(mysql_real_escape_string);
    LOAD_FN(mysql_errno);
    LOAD_FN(mysql_field_count);
    return true;
}

#undef LOAD_FN

// ---------- 工具：SHA-256 (BCrypt) ----------
std::string Sha256Hex(const std::string& input) {
    BCRYPT_ALG_HANDLE hAlg = nullptr;
    BCRYPT_HASH_HANDLE hHash = nullptr;
    unsigned char hash[32] = {};
    DWORD cbHash = 0, cbData = 0;
    std::string out;
    if (BCryptOpenAlgorithmProvider(&hAlg, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0) return out;
    if (BCryptGetProperty(hAlg, BCRYPT_HASH_LENGTH, (PUCHAR)&cbHash, sizeof(cbHash), &cbData, 0) < 0) {
        BCryptCloseAlgorithmProvider(hAlg, 0); return out;
    }
    if (BCryptCreateHash(hAlg, &hHash, nullptr, 0, nullptr, 0, 0) < 0) {
        BCryptCloseAlgorithmProvider(hAlg, 0); return out;
    }
    BCryptHashData(hHash, (PUCHAR)input.data(), (ULONG)input.size(), 0);
    BCryptFinishHash(hHash, hash, sizeof(hash), 0);
    BCryptDestroyHash(hHash);
    BCryptCloseAlgorithmProvider(hAlg, 0);
    char buf[65] = {};
    for (int i = 0; i < 32; ++i) snprintf(buf + i * 2, 3, "%02x", hash[i]);
    out = buf;
    return out;
}

std::string RandomHex(int bytes) {
    std::vector<unsigned char> buf(bytes);
    HCRYPTPROV h = 0;
    CryptAcquireContextW(&h, nullptr, nullptr, PROV_RSA_FULL, CRYPT_VERIFYCONTEXT);
    CryptGenRandom(h, bytes, buf.data());
    CryptReleaseContext(h, 0);
    std::string out;
    char tmp[4];
    for (auto b : buf) { snprintf(tmp, sizeof(tmp), "%02x", b); out += tmp; }
    return out;
}

// 转义字符串（用于 SQL）
std::string SqlEscape(const std::string& s) {
    if (!g_conn || !g_sql.mysql_real_escape_string) return "''";
    std::vector<char> buf(s.size() * 2 + 1);
    g_sql.mysql_real_escape_string(g_conn, buf.data(), s.c_str(), (unsigned long)s.size());
    return std::string(buf.data());
}

// 执行非查询 SQL，失败返回 false
bool ExecSQL(const std::string& sql, std::string* errOut = nullptr) {
    if (!g_conn) { if (errOut) *errOut = "MySQL not connected"; return false; }
    if (g_sql.mysql_query(g_conn, sql.c_str()) != 0) {
        if (errOut) *errOut = g_sql.mysql_error ? g_sql.mysql_error(g_conn) : "query failed";
        return false;
    }
    return true;
}

// 执行查询并取第一行第一列；无结果返回 false
bool QueryScalar(const std::string& sql, std::string& out) {
    if (!g_conn || !g_sql.mysql_query || !g_sql.mysql_store_result || !g_sql.mysql_fetch_row) return false;
    if (g_sql.mysql_query(g_conn, sql.c_str()) != 0) return false;
    void* res = g_sql.mysql_store_result(g_conn);
    if (!res) return false;
    char** row = g_sql.mysql_fetch_row(res);
    bool ok = false;
    if (row && row[0]) { out = row[0]; ok = true; }
    g_sql.mysql_free_result(res);
    return ok;
}

// 时间字符串转 "YYYY-MM-DD HH:MM:SS"
std::string NowSQL() {
    SYSTEMTIME st; GetLocalTime(&st);
    char buf[32];
    snprintf(buf, sizeof(buf), "%04d-%02d-%02d %02d:%02d:%02d",
             st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
    return buf;
}

} // namespace

namespace oj {

bool mysql_available() { return LoadMySql(); }

bool mysql_connect(const std::string& host, unsigned int port,
                   const std::string& user, const std::string& pass,
                   const std::string& db, std::string& err) {
    if (!LoadMySql()) { err = "libmysql.dll 未找到，请安装 MySQL Connector/C 并放到 exe 同目录"; return false; }
    g_conn = g_sql.mysql_init(nullptr);
    if (!g_conn) { err = "mysql_init failed"; return false; }
    g_conn = g_sql.mysql_real_connect(g_conn, host.c_str(), user.c_str(), pass.c_str(),
                                db.c_str(), port, nullptr, 0);
    if (!g_conn) { err = g_sql.mysql_error ? g_sql.mysql_error(g_conn) : "connect failed"; return false; }
    // 设 utf8mb4
    ExecSQL("SET NAMES utf8mb4");
    return true;
}

void mysql_disconnect() {
    if (g_conn && g_sql.mysql_close) g_sql.mysql_close(g_conn);
    g_conn = nullptr;
}

bool mysql_init_schema(std::string& err) {
    const char* ddl[] = {
        R"SQL(CREATE TABLE IF NOT EXISTS users (
  id INT AUTO_INCREMENT PRIMARY KEY,
  username VARCHAR(64) NOT NULL UNIQUE,
  password_hash CHAR(64) NOT NULL,
  salt CHAR(32) NOT NULL,
  role ENUM('admin','author','user') NOT NULL DEFAULT 'user',
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
))SQL",
        R"SQL(CREATE TABLE IF NOT EXISTS sessions (
  token CHAR(64) PRIMARY KEY,
  user_id INT NOT NULL,
  expires_at DATETIME NOT NULL,
  FOREIGN KEY (user_id) REFERENCES users(id)
))SQL",
        R"SQL(CREATE TABLE IF NOT EXISTS submissions (
  id BIGINT AUTO_INCREMENT PRIMARY KEY,
  user_id INT NOT NULL,
  problem_id INT NOT NULL,
  contest_id INT NOT NULL DEFAULT 0,
  verdict VARCHAR(16) NOT NULL,
  detail TEXT,
  time_ms INT,
  `virtual` TINYINT NOT NULL DEFAULT 0,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  UNIQUE KEY uq_user_problem (user_id, problem_id, contest_id),
  FOREIGN KEY (user_id) REFERENCES users(id)
))SQL",
        R"SQL(CREATE TABLE IF NOT EXISTS problem_generators (
  problem_id INT PRIMARY KEY,
  code MEDIUMTEXT NOT NULL,
  version INT NOT NULL DEFAULT 1,
  updated_at DATETIME NOT NULL,
  INDEX idx_gen_updated (updated_at)
))SQL",
        R"SQL(CREATE TABLE IF NOT EXISTS contest_registrations (
  id INT AUTO_INCREMENT PRIMARY KEY,
  user_id INT NOT NULL,
  contest_id INT NOT NULL,
  is_virtual TINYINT NOT NULL DEFAULT 0,
  registered_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  UNIQUE KEY uq_user_contest (user_id, contest_id),
  FOREIGN KEY (user_id) REFERENCES users(id)
))SQL"
    };
    for (auto d : ddl) if (!ExecSQL(d, &err)) return false;

    // ===== 幂等迁移：老库（contest_id 可空 / 无唯一键）升级到"最后提交原地更新" =====
    // 1) contest_id 改为 NOT NULL DEFAULT 0（练习提交 contest_id=0，唯一键才能去重）
    {
        std::string nullable;
        if (QueryScalar("SELECT IS_NULLABLE FROM information_schema.COLUMNS "
                        "WHERE table_schema=DATABASE() AND table_name='submissions' AND column_name='contest_id'",
                        nullable) && nullable == "YES") {
            if (!ExecSQL("UPDATE submissions SET contest_id=0 WHERE contest_id IS NULL", &err)) return false;
            if (!ExecSQL("ALTER TABLE submissions MODIFY contest_id INT NOT NULL DEFAULT 0", &err)) return false;
        }
    }
    // 2) 每人每题（每比赛）去重保留最新一条，再补唯一键
    {
        std::string cnt;
        if (QueryScalar("SELECT COUNT(*) FROM information_schema.STATISTICS "
                        "WHERE table_schema=DATABASE() AND table_name='submissions' AND index_name='uq_user_problem'",
                        cnt) && cnt == "0") {
            if (!ExecSQL("DELETE s1 FROM submissions s1 INNER JOIN submissions s2 "
                         "ON s1.user_id=s2.user_id AND s1.problem_id=s2.problem_id "
                         "AND COALESCE(s1.contest_id,0)=COALESCE(s2.contest_id,0) AND s1.id<s2.id", &err)) return false;
            if (!ExecSQL("ALTER TABLE submissions ADD UNIQUE KEY uq_user_problem (user_id, problem_id, contest_id)", &err)) return false;
        }
    }
    // 3) 匿名默认用户（未登录提交统一归到该账号；空哈希无法登录）
    if (!ExecSQL("INSERT IGNORE INTO users(username,password_hash,salt,role) VALUES('anonymous','','','user')", &err)) return false;
    return true;
}

bool mysql_register(const std::string& username, const std::string& password,
                    const std::string& role, std::string& err) {
    if (username.size() < 2 || username.size() > 64) { err = "用户名长度 2~64"; return false; }
    if (password.size() < 6) { err = "密码至少 6 位"; return false; }
    std::string salt = RandomHex(16);
    std::string hash = Sha256Hex(salt + password);
    std::string sql = "INSERT INTO users(username,password_hash,salt,role) VALUES('"
        + SqlEscape(username) + "','" + hash + "','" + salt + "','"
        + (role == "admin" || role == "author" ? role : "user") + "')";
    if (!ExecSQL(sql, &err)) {
        if (g_sql.mysql_errno && g_sql.mysql_errno(g_conn) == 1062) err = "用户名已被注册";
        return false;
    }
    return true;
}

bool mysql_login(const std::string& username, const std::string& password,
                 std::string& out_token, long long& out_user_id,
                 std::string& out_role, std::string& out_username, std::string& err) {
    std::string sql = "SELECT id, password_hash, salt, role, username FROM users WHERE username='"
        + SqlEscape(username) + "'";
    if (!ExecSQL(sql, &err)) return false;
    void* res = g_sql.mysql_store_result(g_conn);
    if (!res) { err = "no result"; return false; }
    char** row = g_sql.mysql_fetch_row(res);
    if (!row) { g_sql.mysql_free_result(res); err = "用户名或密码错误"; return false; }
    long long uid = atoll(row[0]);
    std::string hashInDb = row[1];
    std::string salt = row[2];
    std::string role = row[3];
    std::string uname = row[4] ? row[4] : username;
    g_sql.mysql_free_result(res);
    if (Sha256Hex(salt + password) != hashInDb) { err = "用户名或密码错误"; return false; }

    out_token = RandomHex(32);
    // 会话 7 天有效
    char exp[32];
    time_t t = time(nullptr) + 7 * 24 * 3600;
    struct tm* lt = localtime(&t);
    strftime(exp, sizeof(exp), "%Y-%m-%d %H:%M:%S", lt);
    std::string ins = "INSERT INTO sessions(token,user_id,expires_at) VALUES('"
        + out_token + "'," + std::to_string(uid) + ",'" + exp + "')";
    if (!ExecSQL(ins, &err)) return false;
    out_user_id = uid;
    out_role = role;
    out_username = uname;
    return true;
}

bool mysql_whoami(const std::string& token, long long& out_user_id,
                  std::string& out_role, std::string& out_username, std::string& err) {
    std::string sql = "SELECT u.id, u.role, u.username FROM sessions s JOIN users u ON u.id=s.user_id "
                      "WHERE s.token='" + SqlEscape(token) + "' AND s.expires_at > NOW()";
    if (!ExecSQL(sql, &err)) return false;
    void* res = g_sql.mysql_store_result(g_conn);
    if (!res) return false;
    char** row = g_sql.mysql_fetch_row(res);
    if (!row) { g_sql.mysql_free_result(res); err = "会话无效或已过期"; return false; }
    out_user_id = atoll(row[0]);
    out_role = row[1] ? row[1] : "user";
    out_username = row[2] ? row[2] : "";
    g_sql.mysql_free_result(res);
    return true;
}

bool mysql_logout(const std::string& token) {
    std::string sql = "DELETE FROM sessions WHERE token='" + SqlEscape(token) + "'";
    return ExecSQL(sql);
}

bool mysql_upsert_submission(long long user_id, int problem_id, int contest_id,
                             const std::string& verdict, const std::string& detail,
                             int time_ms, bool virt, const std::string& ts) {
    // 唯一键 (user_id, problem_id, contest_id)：重复提交时原地更新最后结果，不追加新行
    std::string sql = "INSERT INTO submissions(user_id,problem_id,contest_id,verdict,detail,time_ms,`virtual`,created_at) VALUES("
        + std::to_string(user_id) + "," + std::to_string(problem_id) + ","
        + std::to_string(contest_id > 0 ? contest_id : 0) + ",'"
        + SqlEscape(verdict) + "','" + SqlEscape(detail) + "'," + std::to_string(time_ms) + ","
        + (virt ? "1" : "0") + ",'" + SqlEscape(ts) + "') "
        "ON DUPLICATE KEY UPDATE verdict=VALUES(verdict), detail=VALUES(detail), "
        "time_ms=VALUES(time_ms), `virtual`=VALUES(`virtual`), created_at=VALUES(created_at)";
    return ExecSQL(sql);
}

bool mysql_user_id_by_name(const std::string& username, long long& out_user_id) {
    std::string sql = "SELECT id FROM users WHERE username='" + SqlEscape(username) + "' LIMIT 1";
    std::string v;
    if (!QueryScalar(sql, v)) return false;
    out_user_id = atoll(v.c_str());
    return out_user_id > 0;
}

bool mysql_contest_register(long long user_id, int contest_id, bool virt, std::string& err) {
    // 幂等报名：已存在则更新虚拟标记
    std::string sql = "INSERT INTO contest_registrations(user_id,contest_id,is_virtual) VALUES("
        + std::to_string(user_id) + "," + std::to_string(contest_id) + ","
        + (virt ? "1" : "0") + ") "
        "ON DUPLICATE KEY UPDATE is_virtual=VALUES(is_virtual)";
    return ExecSQL(sql, &err);
}

bool mysql_contest_registration(long long user_id, int contest_id,
                                bool& registered, bool& virt) {
    registered = false; virt = false;
    std::string sql = "SELECT is_virtual FROM contest_registrations WHERE user_id="
        + std::to_string(user_id) + " AND contest_id=" + std::to_string(contest_id) + " LIMIT 1";
    if (!g_conn || !g_sql.mysql_query || !g_sql.mysql_store_result || !g_sql.mysql_fetch_row) return false;
    if (g_sql.mysql_query(g_conn, sql.c_str()) != 0) return false;
    void* res = g_sql.mysql_store_result(g_conn);
    if (!res) return false;
    char** row = g_sql.mysql_fetch_row(res);
    if (row && row[0]) { registered = true; virt = atoi(row[0]) != 0; }
    g_sql.mysql_free_result(res);
    return true;
}
static std::string jsonEscRow(const std::string& s) {
    std::string o;
    for (char c : s) {
        switch (c) {
            case '"':  o += "\\\""; break;
            case '\\': o += "\\\\"; break;
            case '\n': o += "\\n";  break;
            case '\r': break;
            case '\t': o += "\\t";  break;
            default:   o += c;
        }
    }
    return o;
}

bool mysql_contest_submissions(int cid, std::string& out_json) {
    // 每人每题每比赛只保留最后一次结果（submissions 唯一键 upsert 语义），时间倒序
    std::string sql =
        "SELECT s.id, s.problem_id, u.username, s.verdict, s.detail, s.time_ms, "
        "s.`virtual`, DATE_FORMAT(s.created_at, '%Y-%m-%d %H:%i:%s') "
        "FROM submissions s JOIN users u ON u.id=s.user_id "
        "WHERE s.contest_id=" + std::to_string(cid) + " "
        "ORDER BY s.created_at DESC, s.id DESC";
    if (!g_conn || !g_sql.mysql_query || !g_sql.mysql_store_result || !g_sql.mysql_fetch_row) return false;
    if (g_sql.mysql_query(g_conn, sql.c_str()) != 0) return false;
    void* res = g_sql.mysql_store_result(g_conn);
    if (!res) return false;
    out_json = "[";
    bool first = true;
    char** row;
    while ((row = g_sql.mysql_fetch_row(res)) != nullptr) {
        if (!first) out_json += ",";
        first = false;
        out_json += "{\"id\":" + std::string(row[0] ? row[0] : "0")
                 + ",\"problemId\":" + std::string(row[1] ? row[1] : "0")
                 + ",\"username\":\"" + jsonEscRow(row[2] ? row[2] : "") + "\""
                 + ",\"verdict\":\"" + jsonEscRow(row[3] ? row[3] : "") + "\""
                 + ",\"detail\":\"" + jsonEscRow(row[4] ? row[4] : "") + "\""
                 + ",\"timeMs\":" + std::string(row[5] ? row[5] : "0")
                 + ",\"virtual\":" + std::string(atoi(row[6]) ? "true" : "false")
                 + ",\"ts\":\"" + std::string(row[7] ? row[7] : "") + "\"}";
    }
    g_sql.mysql_free_result(res);
    out_json += "]";
    return true;
}
bool mysql_board(int cid, const std::string& start_str,
                const std::string& problems_csv,
                std::string& out_official, std::string& out_virtual) {
    // 按 user + problem 聚合：取该题最早 AC 时间（分钟）+ 错误提交次数*20
    // SQL 直接算好，C++ 再按用户汇总
    std::string sql =
        "SELECT u.username, s.problem_id, s.`virtual`, "
        "  MIN(IF(s.verdict='AC', TIMESTAMPDIFF(MINUTE, '" + SqlEscape(start_str) + "', s.created_at), NULL)) AS ac_min, "
        "  SUM(IF(s.verdict<>'AC', 1, 0)) AS wrong_before "
        "FROM submissions s JOIN users u ON u.id=s.user_id "
        "WHERE s.contest_id=" + std::to_string(cid) +
        " AND s.problem_id IN (" + problems_csv + ") "
        "GROUP BY u.username, s.problem_id, s.virtual "
        "HAVING ac_min IS NOT NULL";
    if (!ExecSQL(sql)) return false;
    void* res = g_sql.mysql_store_result(g_conn);
    if (!res) return false;

    struct Row { std::string username; int virt; long long penalty; };
    std::vector<Row> off, virt_rows;
    char** row;
    while ((row = g_sql.mysql_fetch_row(res)) != nullptr) {
        std::string username = row[0] ? row[0] : "?";
        int v = atoi(row[2]);
        long long acMin = atoll(row[3]);
        long long wrong = atoll(row[4]);
        long long penalty = acMin + wrong * 20;
        Row r{username, v, penalty};
        if (v) virt_rows.push_back(r); else off.push_back(r);
    }
    g_sql.mysql_free_result(res);

    auto emit = [](const std::vector<Row>& rows) -> std::string {
        // 按 username 聚合：solved 数 + penalty 总和，再排序
        std::map<std::string, long long> solved, penalty;
        for (auto& r : rows) { solved[r.username]++; penalty[r.username] += r.penalty; }
        struct O { std::string name; long long s; long long p; };
        std::vector<O> v;
        for (auto& kv : solved) v.push_back({kv.first, kv.second, penalty[kv.first]});
        std::sort(v.begin(), v.end(), [](const O& a, const O& b) {
            if (a.s != b.s) return a.s > b.s;
            return a.p < b.p;
        });
        std::string out = "[";
        for (size_t i = 0; i < v.size(); ++i) {
            if (i) out += ",";
            out += "{\"rank\":" + std::to_string((int)i + 1)
                 + ",\"username\":\"" + v[i].name + "\""
                 + ",\"solved\":" + std::to_string(v[i].s)
                 + ",\"penalty\":" + std::to_string(v[i].p) + "}";
        }
        out += "]";
        return out;
    };
    // username 转义（简单）
    auto esc = [](std::string s) {
        for (auto& c : s) if (c == '"' || c == '\\') c = '_';
        return s;
    };
    (void)esc;
    out_official = emit(off);
    out_virtual = emit(virt_rows);
    return true;
}

bool mysql_list_users(std::string& out_json, std::string& err) {
    std::string sql = "SELECT id, username, role, created_at FROM users ORDER BY id";
    if (!ExecSQL(sql, &err)) return false;
    void* res = g_sql.mysql_store_result(g_conn);
    if (!res) { err = "no result"; return false; }
    out_json = "[";
    bool first = true;
    char** row;
    while ((row = g_sql.mysql_fetch_row(res)) != nullptr) {
        if (!first) out_json += ",";
        first = false;
        out_json += "{\"id\":" + std::string(row[0] ? row[0] : "0")
                  + ",\"username\":\"" + (row[1] ? row[1] : "") + "\""
                  + ",\"role\":\"" + (row[2] ? row[2] : "user") + "\""
                  + ",\"createdAt\":\"" + (row[3] ? row[3] : "") + "\"}";
    }
    g_sql.mysql_free_result(res);
    out_json += "]";
    return true;
}

// ===== 题目数据生成器 =====

bool mysql_gen_upsert(int problem_id, const std::string& code, int version,
                       const std::string& updated_at, std::string& err) {
    std::string sql =
        "INSERT INTO problem_generators(problem_id,code,version,updated_at) VALUES("
        + std::to_string(problem_id) + ",'" + SqlEscape(code) + "',"
        + std::to_string(version) + ",'" + SqlEscape(updated_at) + "') "
        "ON DUPLICATE KEY UPDATE code=VALUES(code), version=VALUES(version), updated_at=VALUES(updated_at)";
    return ExecSQL(sql, &err);
}

bool mysql_gen_get(int problem_id, std::string& out_code, int& out_version,
                    std::string& out_updated_at, std::string& err) {
    std::string sql = "SELECT code, version, updated_at FROM problem_generators WHERE problem_id="
        + std::to_string(problem_id);
    if (!ExecSQL(sql, &err)) return false;
    void* res = g_sql.mysql_store_result(g_conn);
    if (!res) { err = "no result"; return false; }
    char** row = g_sql.mysql_fetch_row(res);
    if (!row) { g_sql.mysql_free_result(res); return false; }
    out_code = row[0] ? row[0] : "";
    out_version = atoi(row[1] ? row[1] : "0");
    out_updated_at = row[2] ? row[2] : "";
    g_sql.mysql_free_result(res);
    return true;
}

bool mysql_gen_search(const std::string& keyword, std::string& out_json, std::string& err) {
    std::string sql = "SELECT problem_id, code, version, updated_at FROM problem_generators ORDER BY problem_id";
    if (!ExecSQL(sql, &err)) return false;
    void* res = g_sql.mysql_store_result(g_conn);
    if (!res) { err = "no result"; return false; }
    // 大小写不敏感匹配
    std::string kw;
    for (char c : keyword) kw += (char)tolower((unsigned char)c);
    out_json = "[";
    bool first = true;
    char** row;
    while ((row = g_sql.mysql_fetch_row(res)) != nullptr) {
        int pid = atoi(row[0] ? row[0] : "0");
        std::string code = row[1] ? row[1] : "";
        int ver = atoi(row[2] ? row[2] : "0");
        std::string ua = row[3] ? row[3] : "";
        std::string codeLow;
        for (char c : code) codeLow += (char)tolower((unsigned char)c);
        if (kw.empty() || codeLow.find(kw) != std::string::npos) {
            // 预览：取前 3 行
            std::string preview;
            int lines = 0;
            for (char c : code) {
                if (c == '\n') { lines++; if (lines >= 3) break; }
                preview += c;
            }
            if (!first) out_json += ",";
            first = false;
            out_json += "{\"problemId\":" + std::to_string(pid)
                      + ",\"version\":" + std::to_string(ver)
                      + ",\"updatedAt\":\"" + ua + "\""
                      + ",\"preview\":\"" + [](const std::string& s){
                            std::string o; for (char c : s) {
                                if (c=='"') o+="\\\""; else if(c=='\\') o+="\\\\";
                                else if(c=='\n') o+="\\n"; else if(c=='\r') o+="\\r";
                                else if(c=='\t') o+="\\t"; else o+=c;
                            } return o;
                        }(preview) + "\"}";
        }
    }
    g_sql.mysql_free_result(res);
    out_json += "]";
    return true;
}

} // namespace oj
