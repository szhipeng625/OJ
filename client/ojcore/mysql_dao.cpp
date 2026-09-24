// mysql_dao.cpp — 数据访问层（统一经中间层 HTTP 网关，不再直连 MySQL）
#include "mysql_dao.h"
#include "http_client.h"
#include "judge.h"
#include "json.hpp"

#include <algorithm>
#include <fstream>
#include <mutex>
#include <sstream>
#include <string>
#include <vector>

#include <cstdio>
#include <cstring>
#include <ctime>
#include <cctype>

using json = nlohmann::json;

namespace {

// ---------- 文件工具（同步题目时把中间层内容物化到本地目录） ----------
void Mkdirs(const std::string& path) {
    std::string cur;
    for (size_t i = 0; i < path.size(); ++i) {
        cur += path[i];
        if (path[i] == '\\' || path[i] == '/') CreateDirectoryA(cur.c_str(), nullptr);
    }
    CreateDirectoryA(path.c_str(), nullptr);
}

void WriteFileBin(const std::string& path, const std::string& content) {
    std::ofstream f(path, std::ios::binary | std::ios::trunc);
    if (f) f.write(content.data(), (std::streamsize)content.size());
}

void RemoveDir(const std::string& dir) {
    if (GetFileAttributesA(dir.c_str()) == INVALID_FILE_ATTRIBUTES) return;
    std::string pattern = dir + "\\*";
    WIN32_FIND_DATAA fd;
    HANDLE hFind = FindFirstFileA(pattern.c_str(), &fd);
    if (hFind != INVALID_HANDLE_VALUE) {
        do {
            std::string name = fd.cFileName;
            if (name == "." || name == "..") continue;
            std::string full = dir + "\\" + name;
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
                RemoveDir(full);
            } else {
                SetFileAttributesA(full.c_str(), FILE_ATTRIBUTE_NORMAL);
                DeleteFileA(full.c_str());
            }
        } while (FindNextFileA(hFind, &fd));
        FindClose(hFind);
    }
    RemoveDirectoryA(dir.c_str());
}

bool ExistsFile(const std::string& p) {
    return GetFileAttributesA(p.c_str()) != INVALID_FILE_ATTRIBUTES;
}

std::string ReadFileText(const std::string& p) {
    std::ifstream f(p, std::ios::binary);
    if (!f) return "";
    std::stringstream ss; ss << f.rdbuf();
    return ss.str();
}

unsigned long long HashString(const std::string& s) {
    unsigned long long h = 5381;
    for (unsigned char c : s) h = h * 33 + c;
    return h;
}

std::string ToHex(unsigned long long v) {
    char buf[24];
    snprintf(buf, sizeof(buf), "%016llx", v);
    return buf;
}

// ---------- 子进程（本地把生成器源码重新编译运行、把标程跑出 .out） ----------
struct ProcResult { bool ok = false; bool timeout = false; int exitCode = 0; std::string errText; };

ProcResult RunRedirect(const std::string& exe, const std::string& args,
                       const std::string& stdinFile, const std::string& stdoutFile,
                       const std::string& workDir, DWORD timeoutMs) {
    ProcResult r;
    SECURITY_ATTRIBUTES sa{ sizeof(sa), NULL, TRUE };
    HANDLE hErrRead = NULL, hErrWrite = NULL;
    if (!CreatePipe(&hErrRead, &hErrWrite, &sa, 0)) { r.errText = "CreatePipe failed"; return r; }
    SetHandleInformation(hErrRead, HANDLE_FLAG_INHERIT, 0);

    HANDLE hIn = NULL;
    if (!stdinFile.empty()) {
        hIn = CreateFileA(stdinFile.c_str(), GENERIC_READ, FILE_SHARE_READ, &sa,
                          OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
        if (hIn == INVALID_HANDLE_VALUE) { CloseHandle(hErrRead); CloseHandle(hErrWrite); r.errText = "无法打开输入文件"; return r; }
    }
    HANDLE hOut = CreateFileA(stdoutFile.c_str(), GENERIC_WRITE,
                              FILE_SHARE_READ | FILE_SHARE_WRITE, &sa,
                              CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (hOut == INVALID_HANDLE_VALUE) {
        if (hIn) CloseHandle(hIn);
        CloseHandle(hErrRead); CloseHandle(hErrWrite);
        r.errText = "无法创建输出文件";
        return r;
    }

    STARTUPINFOA si{ sizeof(si) };
    si.dwFlags = STARTF_USESTDHANDLES;
    si.hStdInput = hIn;
    si.hStdOutput = hOut;
    si.hStdError = hErrWrite;
    PROCESS_INFORMATION pi;
    std::string cmd = "\"" + exe + "\"" + (args.empty() ? "" : " " + args);
    std::vector<char> cmdBuf(cmd.begin(), cmd.end());
    cmdBuf.push_back('\0');
    if (!CreateProcessA(NULL, cmdBuf.data(), NULL, NULL, TRUE, CREATE_NO_WINDOW, NULL,
                        workDir.empty() ? NULL : workDir.c_str(), &si, &pi)) {
        if (hIn) CloseHandle(hIn);
        CloseHandle(hOut); CloseHandle(hErrRead); CloseHandle(hErrWrite);
        r.errText = "启动失败";
        return r;
    }
    CloseHandle(hOut); CloseHandle(hErrWrite);
    if (hIn) CloseHandle(hIn);
    // 先等待进程结束（带超时），再读取 stderr —— 否则长时间运行的进程会让超时判定失效
    DWORD wait = WaitForSingleObject(pi.hProcess, timeoutMs);
    if (wait == WAIT_TIMEOUT) { TerminateProcess(pi.hProcess, 1); r.timeout = true; }
    char buf[8192]; DWORD dwRead;
    while (ReadFile(hErrRead, buf, sizeof(buf), &dwRead, NULL) && dwRead > 0)
        r.errText.append(buf, dwRead);
    CloseHandle(hErrRead);
    GetExitCodeProcess(pi.hProcess, (LPDWORD)&r.exitCode);
    CloseHandle(pi.hProcess); CloseHandle(pi.hThread);
    r.ok = !r.timeout && r.exitCode == 0;
    return r;
}

// 生成器信息（用于本地重新生成判题数据）
// name: 原始生成器名（用于 seedBase 哈希与错误信息）
// dirName: 磁盘目录名（ASCII 安全，避免中文名经 ANSI API 出现编码问题）
struct GenInfo { std::string name; std::string dirName; int count; int seedBase; std::string marker; std::string hash; };

// 重新生成某题的判题数据：编译标程 + 编译运行各生成器（确定性种子）→ .in，再跑标程 → .out。
bool RegenerateProblemData(const std::string& dir, const std::vector<GenInfo>& gens,
                           std::string& errOut) {
    if (gens.empty()) return true;

    std::string stdSrc = dir + "\\std.cpp";
    std::string stdExe = dir + "\\std.exe";
    std::string cerr;
    if (!oj::compile_cpp(stdSrc, stdExe, cerr)) { errOut = "标程编译失败：" + cerr; return false; }

    for (auto& g : gens) {
        std::string gdir = dir + "\\" + g.dirName;
        Mkdirs(gdir);
        std::string gexe = gdir + "\\gen.exe";
        if (!oj::compile_cpp(gdir + "\\gen.cpp", gexe, cerr)) {
            DeleteFileA(stdExe.c_str());
            errOut = "生成器 " + g.name + " 编译失败：" + cerr;
            return false;
        }
        // 清空旧 .in/.out
        for (const char* ext : { ".in", ".out" }) {
            std::string pat = gdir + "\\*" + ext;
            WIN32_FIND_DATAA fd; HANDLE h = FindFirstFileA(pat.c_str(), &fd);
            if (h != INVALID_HANDLE_VALUE) {
                do {
                    if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY))
                        DeleteFileA((gdir + "\\" + fd.cFileName).c_str());
                } while (FindNextFileA(h, &fd));
                FindClose(h);
            }
        }
        // 运行生成器 count 次 → i.in
        for (int i = 1; i <= g.count; ++i) {
            int seed = g.seedBase + i;
            std::string args = "\"" + gdir + "\" " + std::to_string(seed)
                             + " " + std::to_string(g.count) + " " + std::to_string(i);
            std::string outFile = gdir + "\\" + std::to_string(i) + ".in";
            ProcResult rr = RunRedirect(gexe, args, "", outFile, gdir, 60000);
            if (!rr.ok) {
                DeleteFileA(gexe.c_str()); DeleteFileA(stdExe.c_str());
                errOut = "生成器 " + g.name + " 第 " + std::to_string(i) + " 组失败："
                    + (rr.timeout ? "超时" : "退出码 " + std::to_string(rr.exitCode))
                    + (rr.errText.empty() ? "" : "：" + rr.errText.substr(0, 120));
                return false;
            }
        }
        DeleteFileA(gexe.c_str());

        // 跑标程生成 .out（生成阶段给足 60s，避免大数据点被过短超时误杀）
        for (int i = 1; i <= g.count; ++i) {
            std::string inFile = gdir + "\\" + std::to_string(i) + ".in";
            std::string outFile = gdir + "\\" + std::to_string(i) + ".out";
            ProcResult rr = RunRedirect(stdExe, "", inFile, outFile, "", 60000);
            if (!rr.ok) {
                DeleteFileA(stdExe.c_str());
                errOut = "标程运行 " + g.name + "/" + std::to_string(i) + " 失败："
                    + (rr.timeout ? "超时" : "退出码 " + std::to_string(rr.exitCode));
                return false;
            }
        }
    }
    DeleteFileA(stdExe.c_str());
    return true;
}

} // namespace

namespace oj {

// ---------- 中间层状态 ----------
static std::string g_middleware_url;
static std::string g_middleware_token;

void middleware_set_url(const std::string& url) {
    g_middleware_url = url;
    while (!g_middleware_url.empty() && g_middleware_url.back() == '/')
        g_middleware_url.pop_back();
}
const std::string& middleware_url() { return g_middleware_url; }
void middleware_set_token(const std::string& token) { g_middleware_token = token; }
const std::string& middleware_token() { return g_middleware_token; }

static std::string UrlEncode(const std::string& s) {
    std::string out;
    char buf[4];
    for (unsigned char c : s) {
        if (isalnum(c) || c == '-' || c == '_' || c == '.' || c == '~') out += (char)c;
        else { snprintf(buf, sizeof(buf), "%%%02X", c); out += buf; }
    }
    return out;
}

// ---------- JSON 取值工具 ----------
static std::string jstr(const json& j, const char* key) {
    auto it = j.find(key);
    return (it != j.end() && it->is_string()) ? it->get<std::string>() : std::string();
}
static long long jint(const json& j, const char* key, long long def = 0) {
    auto it = j.find(key);
    return (it != j.end() && it->is_number()) ? it->get<long long>() : def;
}
static bool jbool(const json& j, const char* key) {
    auto it = j.find(key);
    return (it != j.end() && it->is_boolean()) ? it->get<bool>() : false;
}

// ---------- HTTP 请求 ----------
static bool mw_req(const std::string& method, const std::string& path,
                   const std::string& body, std::string& resp, std::string& err,
                   int* status = nullptr) {
    HttpResp r;
    if (!http_request(method, g_middleware_url, path, body, g_middleware_token, r, err)) return false;
    resp = r.body;
    if (status) *status = r.status;
    return true;
}

static bool mw_ok(const std::string& resp, const std::string& fallback, std::string& err) {
    try {
        json j = json::parse(resp);
        if (j.value("ok", false)) return true;
        auto it = j.find("error");
        err = (it != j.end() && it->is_string()) ? it->get<std::string>() : fallback;
    } catch (...) { err = fallback; }
    return false;
}

// ---------- 中间层模式下把题目/比赛物化到本地 ----------
static bool middleware_sync_problems(const std::string& problem_dir, const std::string& server_root,
                                     std::string& err) {
    if (problem_dir.empty()) { err = "题目目录为空"; return false; }
    CreateDirectoryA(problem_dir.c_str(), nullptr);

    HttpResp r; std::string e;
    if (!http_request("GET", g_middleware_url, "/api/problems", "", "", r, e) || r.status != 200) {
        err = "获取题目列表失败: " + (e.empty() ? ("HTTP " + std::to_string(r.status)) : e);
        return false;
    }

    std::vector<int> ids;
    try {
        json arr = json::parse(r.body);
        if (arr.is_array())
            for (auto& it : arr) {
                int id = it.value("id", 0);
                bool pub = it.value("isPublic", false);
                if (id > 0 && pub) ids.push_back(id);
            }
    } catch (...) { err = "题目列表解析失败"; return false; }

    for (int id : ids) {
        HttpResp pr;
        if (!http_request("GET", g_middleware_url, "/api/problem?id=" + std::to_string(id), "", "", pr, e)
            || pr.status != 200) continue;
        json p;
        try { p = json::parse(pr.body); } catch (...) { continue; }
        if (!p.value("ok", false)) continue;

        std::string title = p.value("title", "");
        std::string desc = p.value("description", "");
        std::string sampleIn = p.value("sampleIn", "");
        std::string sampleOut = p.value("sampleOut", "");
        int timeMs = p.value("timeMs", 1000); if (timeMs <= 0) timeMs = 1000;
        int memMb = p.value("memMb", 256); if (memMb <= 0) memMb = 256;
        std::string tags = p.value("tags", json::array()).dump();
        std::string stdCode = p.value("stdCode", "");

        std::string dir = problem_dir + "\\" + std::to_string(id);
        Mkdirs(dir);
        WriteFileBin(dir + "\\statement.txt", title + "\n" + desc);
        WriteFileBin(dir + "\\sample.in", sampleIn);
        WriteFileBin(dir + "\\sample.out", sampleOut);
        std::string meta = "{\"timeLimitMs\":" + std::to_string(timeMs)
            + ",\"memLimitMB\":" + std::to_string(memMb)
            + ",\"tags\":" + (tags.empty() ? "[]" : tags) + "}";
        WriteFileBin(dir + "\\meta.json", meta);
        if (!stdCode.empty()) WriteFileBin(dir + "\\std.cpp", stdCode);

        // 仅物化生成器源码与元数据（不生成数据）；数据在判题时按需生成（ensure_problem_data）
        std::vector<std::string> knownDirs;
        for (auto& g : p.value("generators", json::array())) {
            std::string name = g.value("name", "");
            int count = g.value("genCount", 0);
            int gid = g.value("id", 0);
            std::string code = g.value("code", "");
            std::string gdesc = g.value("description", "");
            if (name.empty() || count <= 0) continue;

            // 确定性 seedBase（与中间层 bind_generator 同公式）
            unsigned long long gh = HashString(name);
            int seedBase = (int)(((unsigned long long)id * 1000003ull + gh) & 0x7fffffff);

            // 目录名用生成器 id（纯 ASCII），避免中文名经 ANSI 文件 API 出现编码问题
            std::string dirName = gid > 0 ? ("g" + std::to_string(gid))
                                          : ("g_" + ToHex(gh));
            std::string gdir = dir + "\\" + dirName;
            Mkdirs(gdir);
            WriteFileBin(gdir + "\\gen.cpp", code);
            WriteFileBin(gdir + "\\desc.txt", gdesc);
            // 持久化生成参数，供判题时按需生成数据
            json gm = {{"count", count}, {"seedBase", seedBase}, {"name", name}};
            WriteFileBin(gdir + "\\genmeta.txt", gm.dump());
            knownDirs.push_back(dirName);
        }
        // 清理已删除的生成器目录
        {
            std::string pat = dir + "\\*";
            WIN32_FIND_DATAA fd; HANDLE h = FindFirstFileA(pat.c_str(), &fd);
            if (h != INVALID_HANDLE_VALUE) {
                do {
                    if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) continue;
                    std::string n = fd.cFileName;
                    if (n == "." || n == "..") continue;
                    bool known = false;
                    for (auto& kd : knownDirs) if (kd == n) { known = true; break; }
                    if (!known && ExistsFile(dir + "\\" + n + "\\gen.cpp"))
                        RemoveDir(dir + "\\" + n);
                } while (FindNextFileA(h, &fd));
                FindClose(h);
            }
        }

        // 题目测试样例（丰富题面/调试用）：物化到 testcases.json
        {
            HttpResp tr;
            if (http_request("GET", g_middleware_url, "/api/testcases?problemId=" + std::to_string(id), "", "", tr, e) && tr.status == 200) {
                try {
                    json tj = json::object();
                    tj["cases"] = json::parse(tr.body);
                    WriteFileBin(dir + "\\testcases.json", tj.dump());
                } catch (...) {}
            }
        }
    }

    // 比赛
    if (!server_root.empty()) {
        HttpResp cr;
        if (http_request("GET", g_middleware_url, "/api/contests", "", "", cr, e) && cr.status == 200) {
            try {
                json arr = json::parse(cr.body);
                if (arr.is_array())
                    for (auto& c : arr) {
                        int cid = c.value("id", 0);
                        std::string content = c.value("content", "");
                        if (cid <= 0 || content.empty()) continue;
                        std::string cdir = server_root + "\\contests\\" + std::to_string(cid);
                        Mkdirs(cdir);
                        WriteFileBin(cdir + "\\contest.json", content);
                    }
            } catch (...) {}
        }
    }
    return true;
}

bool mysql_available() { return !g_middleware_url.empty(); }

bool mysql_connect(const std::string& host, unsigned int port,
                   const std::string& user, const std::string& pass,
                   const std::string& db, std::string& err) {
    (void)host; (void)port; (void)user; (void)pass; (void)db;
    err = "已切换到中间层模式，不再直连 MySQL";
    return false;
}

bool mysql_init_schema(std::string& err) {
    (void)err;
    return true;  // schema 由中间层管理
}

// ---------- 认证 ----------
bool mysql_register(const std::string& username, const std::string& password,
                    const std::string& email, const std::string& name,
                    const std::string& school, std::string& err) {
    json b{{"username", username}, {"password", password},
           {"email", email}, {"name", name}, {"school", school}};
    std::string resp, e;
    if (!mw_req("POST", "/api/register", b.dump(), resp, e)) { err = e; return false; }
    return mw_ok(resp, "注册失败", err);
}

bool mysql_login(const std::string& username, const std::string& password,
                 std::string& out_token, long long& out_user_id,
                 std::string& out_role, std::string& out_username,
                 std::string& out_nickname, std::string& out_avatar,
                 std::string& out_email, std::string& out_name,
                 std::string& out_school, std::string& err) {
    json b{{"username", username}, {"password", password}};
    std::string resp, e;
    if (!mw_req("POST", "/api/login", b.dump(), resp, e)) { err = e; return false; }
    if (!mw_ok(resp, "用户名或密码错误", err)) return false;
    json j = json::parse(resp);
    out_token = jstr(j, "token");
    out_user_id = jint(j, "userId");
    out_role = jstr(j, "role"); if (out_role.empty()) out_role = "user";
    out_username = jstr(j, "username");
    out_nickname = jstr(j, "nickname");
    out_avatar = jstr(j, "avatar");
    out_email = jstr(j, "email");
    out_name = jstr(j, "name");
    out_school = jstr(j, "school");
    g_middleware_token = out_token;
    return !out_token.empty();
}

bool mysql_whoami(const std::string& token, long long& out_user_id,
                  std::string& out_role, std::string& out_username,
                  std::string& out_nickname, std::string& out_avatar,
                  std::string& out_email, std::string& out_name,
                  std::string& out_school, std::string& err) {
    g_middleware_token = token;
    std::string resp, e;
    if (!mw_req("GET", "/api/whoami", "", resp, e)) { err = e; return false; }
    if (!mw_ok(resp, "会话无效或已过期", err)) return false;
    json j = json::parse(resp);
    out_user_id = jint(j, "userId");
    out_role = jstr(j, "role"); if (out_role.empty()) out_role = "user";
    out_username = jstr(j, "username");
    out_nickname = jstr(j, "nickname");
    out_avatar = jstr(j, "avatar");
    out_email = jstr(j, "email");
    out_name = jstr(j, "name");
    out_school = jstr(j, "school");
    return true;
}

bool mysql_logout(const std::string& token) {
    std::string resp, e;
    mw_req("POST", "/api/logout", "{}", resp, e);
    g_middleware_token.clear();
    return true;
}

bool mysql_update_profile(long long user_id, const std::string& nickname,
                          const std::string& avatar, std::string& err) {
    (void)user_id;
    json b{{"nickname", nickname}, {"avatar", avatar}};
    std::string resp, e;
    if (!mw_req("POST", "/api/profile", b.dump(), resp, e)) { err = e; return false; }
    return mw_ok(resp, "更新失败", err);
}

// ---------- 提交 / 进度 / 榜单 ----------
long long mysql_upsert_submission(int problem_id, int contest_id,
                                  const std::string& verdict, const std::string& detail,
                                  int time_ms, bool virt, const std::string& ts,
                                  const std::string& code) {
    int cid = contest_id > 0 ? contest_id : 0;
    json b{{"problemId", problem_id}, {"contestId", cid}, {"verdict", verdict},
           {"detail", detail}, {"timeMs", time_ms}, {"virtual", virt}, {"ts", ts},
           {"code", code}};
    std::string resp, e;
    if (!mw_req("POST", "/api/submit", b.dump(), resp, e)) return 0;
    try {
        json j = json::parse(resp);
        if (!j.value("ok", false)) return 0;
        long long sid = jint(j, "sid");
        return sid > 0 ? sid : 0;
    } catch (...) { return 0; }
}

bool mysql_user_id_by_name(const std::string& username, long long& out_user_id) {
    std::string resp, e;
    if (!mw_req("GET", "/api/user_id?username=" + UrlEncode(username), "", resp, e)) return false;
    try {
        json j = json::parse(resp);
        if (!j.value("ok", false)) return false;
        out_user_id = jint(j, "userId");
        return out_user_id > 0;
    } catch (...) { return false; }
}

bool mysql_user_progress(const std::string& username, int contest_id, std::string& out_json) {
    std::string resp, e;
    if (!mw_req("GET", "/api/progress?username=" + UrlEncode(username) + "&cid=" + std::to_string(contest_id),
                "", resp, e)) return false;
    if (resp.empty() || resp[0] != '[') return false;
    out_json = resp;
    return true;
}

bool mysql_user_solution(int problem_id, int contest_id, std::string& out_json) {
    std::string q = "/api/user_solution?problemId=" + std::to_string(problem_id)
        + "&contestId=" + std::to_string(contest_id);
    std::string resp, e;
    if (!mw_req("GET", q, "", resp, e)) return false;
    if (resp.empty()) return false;
    out_json = resp;
    return true;
}

bool mysql_contest_register(long long user_id, int contest_id, bool virt, std::string& err) {
    (void)user_id;
    json b{{"contestId", contest_id}, {"virtual", virt}};
    std::string resp, e;
    if (!mw_req("POST", "/api/contest/register", b.dump(), resp, e)) { err = e; return false; }
    return mw_ok(resp, "报名失败", err);
}

bool mysql_contest_registration(long long user_id, int contest_id,
                                bool& registered, bool& virt) {
    (void)user_id; (void)contest_id;
    // 中间层暂无“报名状态查询”接口，返回未报名，由上层回退到 LSM 凭证
    registered = false;
    virt = false;
    return false;
}

bool mysql_contest_submissions(int cid, const std::string& username, bool view_all,
                               std::string& out_json) {
    std::string q = "/api/contest/submissions?cid=" + std::to_string(cid)
        + "&username=" + UrlEncode(username)
        + "&view_all=" + (view_all ? "1" : "0");
    std::string resp, e;
    if (!mw_req("GET", q, "", resp, e)) return false;
    out_json = resp;
    return true;
}

bool mysql_board(int cid, const std::string& start_str,
                 const std::string& problems_csv,
                 std::string& out_official, std::string& out_virtual) {
    std::string q = "/api/board?cid=" + std::to_string(cid)
        + "&start=" + UrlEncode(start_str)
        + "&problems=" + UrlEncode(problems_csv);
    std::string resp, e;
    if (!mw_req("GET", q, "", resp, e)) return false;
    try {
        json j = json::parse(resp);
        out_official = j.value("official", json::array()).dump();
        out_virtual = j.value("virtual", json::array()).dump();
        return true;
    } catch (...) { return false; }
}

bool mysql_list_users(std::string& out_json, std::string& err) {
    std::string resp, e;
    if (!mw_req("GET", "/api/users", "", resp, e)) { err = e; return false; }
    out_json = resp;
    return true;
}

// ---------- 题目 / 比赛 ----------
bool mysql_upsert_problem(int id, const std::string& title, const std::string& desc,
                          const std::string& sample_in, const std::string& sample_out,
                          int time_ms, int mem_mb, const std::string& tags_json,
                          const std::string& std_code, bool is_public, std::string& err) {
    int t = time_ms > 0 ? time_ms : 1000;
    int m = mem_mb > 0 ? mem_mb : 256;
    std::string tags = tags_json.empty() ? "[]" : tags_json;
    json b{{"id", id}, {"title", title}, {"description", desc}, {"sampleIn", sample_in},
           {"sampleOut", sample_out}, {"timeMs", t}, {"memMb", m}, {"tags", tags},
           {"stdCode", std_code}, {"isPublic", is_public}};
    std::string resp, e;
    if (!mw_req("POST", "/api/problem/upsert", b.dump(), resp, e)) { err = e; return false; }
    return mw_ok(resp, "发布失败", err);
}

bool mysql_problem_visibility(std::string& out_json) {
    std::string resp, e;
    if (!mw_req("GET", "/api/problem_visibility", "", resp, e)) return false;
    out_json = resp;
    return true;
}

bool mysql_list_problems(std::string& out_json) {
    std::string resp, e;
    if (!mw_req("GET", "/api/problems", "", resp, e)) return false;
    out_json = resp;
    return true;
}

bool mysql_get_problem(int id, std::string& out_json) {
    std::string resp, e;
    if (!mw_req("GET", "/api/problem?id=" + std::to_string(id), "", resp, e)) return false;
    out_json = resp;
    return true;
}

bool mysql_upsert_contest(int cid, const std::string& contest_json, std::string& err) {
    json b{{"id", cid}, {"content", contest_json}};
    std::string resp, e;
    if (!mw_req("POST", "/api/contest/upsert", b.dump(), resp, e)) { err = e; return false; }
    return mw_ok(resp, "发布比赛失败", err);
}

bool mysql_list_contests(std::string& out_json) {
    std::string resp, e;
    if (!mw_req("GET", "/api/contests", "", resp, e)) return false;
    try {
        json arr = json::parse(resp);
        json out = json::array();
        if (arr.is_array()) {
            for (auto& c : arr) {
                int cid = c.value("id", 0);
                std::string content = c.value("content", "");
                if (cid <= 0 || content.empty()) continue;
                try {
                    json cc = json::parse(content);
                    auto p = cc.value("problems", json::array());
                    int pc = p.is_array() ? (int)p.size() : 0;
                    out.push_back({{"id", cid}, {"name", cc.value("name", "")},
                                   {"problemCount", pc},
                                   {"startTime", cc.value("startTime", "")},
                                   {"endTime", cc.value("endTime", "")}});
                } catch (...) { continue; }
            }
        }
        out_json = out.dump();
        return true;
    } catch (...) { return false; }
}

bool mysql_get_contest(int cid, std::string& out_json) {
    std::string resp, e;
    if (!mw_req("GET", "/api/contest?id=" + std::to_string(cid), "", resp, e)) return false;
    try {
        json j = json::parse(resp);
        if (!j.value("ok", false)) return false;
        std::string content = j.value("content", "");
        if (content.empty()) return false;
        json cc = json::parse(content);
        out_json = (json{{"ok", true}, {"id", cid}, {"name", cc.value("name", "")},
                         {"description", cc.value("description", "")},
                         {"startTime", cc.value("startTime", "")},
                         {"endTime", cc.value("endTime", "")},
                         {"problems", cc.value("problems", json::array())}}).dump();
        return true;
    } catch (...) { return false; }
}

bool mysql_sync_problems(const std::string& problem_dir, const std::string& server_root, std::string& err) {
    return middleware_sync_problems(problem_dir, server_root, err);
}

// 判题前按需生成数据：读本地 gen.cpp / genmeta.txt / std.cpp，缓存过期或缺失时重新生成。
// 生成耗时发生在 run_tests 之前，不计入判题（测试点）计时。
bool ensure_problem_data(int problem_id, const std::string& problem_dir, std::string& errOut) {
    static std::mutex g_gen_mtx;
    std::lock_guard<std::mutex> lock(g_gen_mtx);

    std::string dir = problem_dir + "\\" + std::to_string(problem_id);
    if (!ExistsFile(dir)) { errOut = "题目目录不存在"; return false; }
    std::string stdCode = ReadFileText(dir + "\\std.cpp");
    if (stdCode.empty()) { errOut = "标程不存在"; return false; }

    std::vector<GenInfo> regenGens;
    std::string pat = dir + "\\*";
    WIN32_FIND_DATAA fd; HANDLE h = FindFirstFileA(pat.c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) { errOut = "枚举题目目录失败"; return false; }
    do {
        if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) continue;
        std::string dn = fd.cFileName;
        if (dn == "." || dn == "..") continue;
        std::string gdir = dir + "\\" + dn;
        std::string metaPath = gdir + "\\genmeta.txt";
        if (!ExistsFile(metaPath)) continue;   // 不是生成器目录（无 genmeta.txt）
        json mj;
        try { mj = json::parse(ReadFileText(metaPath)); } catch (...) { continue; }
        int count = mj.value("count", 0);
        int seedBase = mj.value("seedBase", 0);
        std::string name = mj.value("name", "");
        std::string code = ReadFileText(gdir + "\\gen.cpp");
        if (count <= 0 || code.empty()) continue;

        std::string hashInput = code + "|" + std::to_string(count)
            + "|" + std::to_string(seedBase) + "|" + stdCode;
        GenInfo gi;
        gi.name = name; gi.dirName = dn; gi.count = count; gi.seedBase = seedBase;
        gi.hash = ToHex(HashString(hashInput));
        gi.marker = gdir + "\\.genhash";
        bool complete = ExistsFile(gdir + "\\" + std::to_string(count) + ".in")
                     && ExistsFile(gdir + "\\" + std::to_string(count) + ".out");
        if (ReadFileText(gi.marker) != gi.hash || !complete)
            regenGens.push_back(gi);
    } while (FindNextFileA(h, &fd));
    FindClose(h);

    if (regenGens.empty()) { errOut.clear(); return true; }
    std::string rerr;
    if (!RegenerateProblemData(dir, regenGens, rerr)) { errOut = rerr; return false; }
    for (auto& gi : regenGens) WriteFileBin(gi.marker, gi.hash);
    errOut.clear();
    return true;
}

// ---------- 生成器 ----------
bool mysql_list_generators(std::string& out_json) {
    std::string resp, e;
    if (!mw_req("GET", "/api/generators", "", resp, e)) return false;
    out_json = resp;
    return true;
}

bool mysql_get_generator(int id, std::string& out_name, std::string& out_code, std::string& out_desc) {
    std::string resp, e;
    if (!mw_req("GET", "/api/generator?id=" + std::to_string(id), "", resp, e)) return false;
    try {
        json j = json::parse(resp);
        if (!j.value("ok", false)) return false;
        out_name = jstr(j, "name");
        out_code = jstr(j, "code");
        out_desc = jstr(j, "description");
        return true;
    } catch (...) { return false; }
}

bool mysql_create_generator(const std::string& name, const std::string& code, const std::string& desc,
                            long long& out_id, std::string& err) {
    json b{{"name", name}, {"code", code}, {"description", desc}};
    std::string resp, e;
    if (!mw_req("POST", "/api/generator/create", b.dump(), resp, e)) { err = e; return false; }
    if (!mw_ok(resp, "新建生成器失败", err)) return false;
    try { out_id = jint(json::parse(resp), "id"); } catch (...) { out_id = 0; }
    return out_id > 0;
}

bool mysql_update_generator(int id, const std::string& code, const std::string& desc, std::string& err) {
    json b{{"id", id}, {"code", code}, {"description", desc}};
    std::string resp, e;
    if (!mw_req("POST", "/api/generator/update", b.dump(), resp, e)) { err = e; return false; }
    return mw_ok(resp, "更新生成器失败", err);
}

bool mysql_delete_generator(int id) {
    json b{{"id", id}};
    std::string resp, e;
    if (!mw_req("POST", "/api/generator/delete", b.dump(), resp, e)) return false;
    return mw_ok(resp, "删除生成器失败", e);
}

bool mysql_problem_generators(int problem_id, std::string& out_json) {
    // 中间层无“题目已绑生成器”接口，从题目详情里取 generators 数组并裁剪为 {id,name,genCount}
    std::string resp, e;
    if (!mw_req("GET", "/api/problem?id=" + std::to_string(problem_id), "", resp, e)) return false;
    try {
        json p = json::parse(resp);
        if (!p.value("ok", false)) return false;
        json arr = json::array();
        for (auto& g : p.value("generators", json::array()))
            arr.push_back({{"id", g.value("id", 0)}, {"name", g.value("name", "")}, {"genCount", g.value("genCount", 0)}});
        out_json = arr.dump();
        return true;
    } catch (...) { return false; }
}

bool mysql_bind_generator(int problem_id, int generator_id, int gen_count, std::string& err) {
    json b{{"problemId", problem_id}, {"generatorId", generator_id}, {"genCount", gen_count > 0 ? gen_count : 10}};
    std::string resp, e;
    if (!mw_req("POST", "/api/generator/bind", b.dump(), resp, e)) { err = e; return false; }
    return mw_ok(resp, "绑定失败", err);
}

bool mysql_unbind_generator(int problem_id, int generator_id) {
    json b{{"problemId", problem_id}, {"generatorId", generator_id}};
    std::string resp, e;
    if (!mw_req("POST", "/api/generator/unbind", b.dump(), resp, e)) return false;
    try { return json::parse(resp).value("ok", false); } catch (...) { return false; }
}

bool mysql_search_generators(const std::string& keyword, std::string& out_json) {
    std::string resp, e;
    if (!mw_req("GET", "/api/generator/search?q=" + UrlEncode(keyword), "", resp, e)) return false;
    out_json = resp;
    return true;
}

// ---------- 测试样例 ----------
bool mysql_set_testcase(int problem_id, const std::string& name,
                        const std::string& input, const std::string& output,
                        bool is_sample) {
    json b{{"problemId", problem_id}, {"name", name}, {"input", input},
           {"output", output}, {"isSample", is_sample}};
    std::string resp, e;
    if (!mw_req("POST", "/api/testcase/set", b.dump(), resp, e)) return false;
    try { return json::parse(resp).value("ok", false); } catch (...) { return false; }
}

bool mysql_remove_testcase(int problem_id, const std::string& name) {
    json b{{"problemId", problem_id}, {"name", name}};
    std::string resp, e;
    if (!mw_req("POST", "/api/testcase/remove", b.dump(), resp, e)) return false;
    try { return json::parse(resp).value("ok", false); } catch (...) { return false; }
}

bool mysql_list_testcases(int problem_id, std::string& out_json) {
    std::string resp, e;
    if (!mw_req("GET", "/api/testcases?problemId=" + std::to_string(problem_id), "", resp, e)) return false;
    out_json = resp;
    return true;
}

} // namespace oj
