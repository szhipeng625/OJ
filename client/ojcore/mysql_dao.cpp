// mysql_dao.cpp — 动态加载 libmysql.dll，封装用户/会话/提交/榜单/题目分发
#include "mysql_dao.h"
#include "judge.h"

#include <windows.h>
#include <bcrypt.h>
#include <wincrypt.h>

#include <algorithm>
#include <fstream>
#include <map>
#include <set>
#include <sstream>
#include <string>
#include <vector>

#include <cstdio>
#include <cstring>
#include <ctime>
#include <cctype>
#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "advapi32.lib")

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

// ---------- base64（测试数据文件作为 LONGBLOB 前先转文本，避免二进制/字符集问题） ----------
static const char* B64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

std::string Base64Encode(const std::string& in) {
    std::string out;
    out.reserve(((in.size() + 2) / 3) * 4);
    int val = 0, bits = -6;
    for (unsigned char c : in) {
        val = (val << 8) + c;
        bits += 8;
        while (bits >= 0) { out += B64[(val >> bits) & 0x3F]; bits -= 6; }
    }
    if (bits > -6) out += B64[((val << 8) >> (bits + 8)) & 0x3F];
    while (out.size() % 4) out += '=';
    return out;
}

std::string Base64Decode(const std::string& in) {
    int table[256] = {};
    for (int i = 0; i < 64; ++i) table[(unsigned char)B64[i]] = i;
    std::string out;
    int val = 0, bits = -8;
    for (unsigned char c : in) {
        if (c == '=') break;
        int v = table[c];
        if (v == 0 && c != 'A') continue;   // 忽略非法字符
        val = (val << 6) + v;
        bits += 6;
        if (bits >= 0) { out += (char)((val >> bits) & 0xFF); bits -= 8; }
    }
    return out;
}

// ---------- 文件工具（同步题目时把 DB 内容物化到本地目录） ----------
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
    std::string cmd = "rmdir /s /q \"" + dir + "\"";
    system(cmd.c_str());
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

// djb2 字符串哈希（仅用于生成器的变更检测 / 确定性种子，无需密码学强度）
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

// ---------- AES-256-CBC 加密/解密（生成器源码与标程在库里密文存储，客户端解密后使用） ----------
static const unsigned char kAesKey[32] = {
    0x6f,0x6a,0x5f,0x63,0x6f,0x72,0x65,0x5f,0x32,0x30,0x32,0x36,0x6b,0x65,0x79,0x21,
    0x4f,0x4a,0x2d,0x45,0x4e,0x43,0x52,0x59,0x50,0x54,0x2d,0x4b,0x45,0x59,0x00,0x31
};
static const unsigned char kAesIv[16] = {
    0x6f,0x6a,0x2d,0x69,0x76,0x2d,0x31,0x36,0x62,0x79,0x74,0x65,0x73,0x21,0x21,0x21
};

bool AesCrypt(bool encrypt, const std::string& input, std::string& output) {
    if (input.empty()) { output.clear(); return true; }
    HCRYPTPROV prov = 0;
    HCRYPTKEY key = 0;
    if (!CryptAcquireContextW(&prov, NULL, NULL, PROV_RSA_AES, CRYPT_VERIFYCONTEXT))
        return false;
    struct AesKeyBlob {
        BLOBHEADER hdr;
        DWORD keySize;
        BYTE keyBytes[32];
    } kb;
    kb.hdr.bType = PLAINTEXTKEYBLOB;
    kb.hdr.bVersion = CUR_BLOB_VERSION;
    kb.hdr.reserved = 0;
    kb.hdr.aiKeyAlg = CALG_AES_256;
    kb.keySize = 32;
    memcpy(kb.keyBytes, kAesKey, 32);
    if (!CryptImportKey(prov, (BYTE*)&kb, sizeof(kb), 0, 0, &key)) {
        CryptReleaseContext(prov, 0);
        return false;
    }
    DWORD mode = CRYPT_MODE_CBC;
    CryptSetKeyParam(key, KP_MODE, (BYTE*)&mode, 0);
    CryptSetKeyParam(key, KP_IV, (BYTE*)kAesIv, 0);
    DWORD pad = PKCS5_PADDING;
    CryptSetKeyParam(key, KP_PADDING, (BYTE*)&pad, 0);

    std::vector<BYTE> buf(input.begin(), input.end());
    bool ok = false;
    if (encrypt) {
        DWORD len = (DWORD)buf.size();
        buf.resize(len + 16);
        if (CryptEncrypt(key, 0, TRUE, 0, buf.data(), &len, (DWORD)buf.size())) {
            buf.resize(len);
            ok = true;
        }
    } else {
        DWORD len = (DWORD)buf.size();
        if (CryptDecrypt(key, 0, TRUE, 0, buf.data(), &len)) {
            buf.resize(len);
            ok = true;
        }
    }
    CryptDestroyKey(key);
    CryptReleaseContext(prov, 0);
    if (ok) output.assign((char*)buf.data(), buf.size());
    return ok;
}

// 运行子进程：stdin 从 stdinFile（可为空），stdout 重定向到 stdoutFile，stderr 捕获。
// 用于客户端本地把「生成器源码」重新编译运行、把「标程」跑出 .out。
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
struct GenInfo { std::string name; int count; int seedBase; std::string marker; std::string hash; };

// 重新生成某题的判题数据：编译标程 + 编译运行各生成器（确定性种子）→ .in，再跑标程 → .out。
// 生成器协议：每个测试点独立运行一次，stdout 重定向到 i.in；
// argv[1]=输出目录, argv[2]=随机种子, argv[3]=总组数 n, argv[4]=当前组号 i。
bool RegenerateProblemData(const std::string& dir, const std::vector<GenInfo>& gens,
                           std::string& errOut) {
    if (gens.empty()) return true;

    std::string stdSrc = dir + "\\std.cpp";
    std::string stdExe = dir + "\\std.exe";
    std::string cerr;
    if (!oj::compile_cpp(stdSrc, stdExe, cerr)) { errOut = "标程编译失败：" + cerr; return false; }

    for (auto& g : gens) {
        std::string gdir = dir + "\\" + g.name;
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

// 生成器库 schema 迁移：
//   generators 增加 name/updated_at 并加唯一名；problem_generators 删除 name 列（旧列数据先备份到 generators.name）。
bool migrate_generator_schema(std::string& err) {
    // 1) generators.name
    {
        std::string has;
        QueryScalar("SELECT COUNT(*) FROM information_schema.COLUMNS "
                    "WHERE table_schema=DATABASE() AND table_name='generators' AND column_name='name'", has);
        if (has != "1" && !ExecSQL("ALTER TABLE generators ADD COLUMN name VARCHAR(64) NOT NULL DEFAULT ''", &err))
            return false;
    }
    // 2) generators.updated_at
    {
        std::string has;
        QueryScalar("SELECT COUNT(*) FROM information_schema.COLUMNS "
                    "WHERE table_schema=DATABASE() AND table_name='generators' AND column_name='updated_at'", has);
        if (has != "1" && !ExecSQL("ALTER TABLE generators ADD COLUMN updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP", &err))
            return false;
    }
    // 3) 回填 name：优先取旧 problem_generators.name，否则 gen_<id>
    {
        std::string hasPgName;
        QueryScalar("SELECT COUNT(*) FROM information_schema.COLUMNS "
                    "WHERE table_schema=DATABASE() AND table_name='problem_generators' AND column_name='name'", hasPgName);
        if (hasPgName == "1") {
            ExecSQL("UPDATE generators g JOIN problem_generators pg ON pg.generator_id=g.id "
                    "SET g.name=pg.name WHERE (g.name='' OR g.name IS NULL) AND pg.name IS NOT NULL AND pg.name<>''");
        }
        ExecSQL("UPDATE generators SET name=CONCAT('gen_', id) WHERE name='' OR name IS NULL");
    }
    // 4) 去重：同名保留最小 id，其余改名为 name_id
    {
        std::string dup;
        QueryScalar("SELECT COUNT(*) FROM (SELECT name FROM generators WHERE name<>'' GROUP BY name HAVING COUNT(*)>1) t", dup);
        if (dup == "1" || (dup != "0" && !dup.empty())) {
            if (g_sql.mysql_query(g_conn, "SELECT id,name FROM generators WHERE name<>'' ORDER BY id") == 0) {
                void* res = g_sql.mysql_store_result(g_conn);
                if (res) {
                    std::map<std::string, int> seen;
                    std::vector<std::pair<int, std::string>> renames;
                    char** row;
                    while ((row = g_sql.mysql_fetch_row(res)) != nullptr) {
                        int id = atoi(row[0]);
                        std::string nm = row[1] ? row[1] : "";
                        if (nm.empty()) continue;
                        int& c = seen[nm];
                        ++c;
                        if (c > 1) renames.push_back({ id, nm + "_" + std::to_string(id) });
                    }
                    g_sql.mysql_free_result(res);
                    for (auto& r : renames)
                        ExecSQL("UPDATE generators SET name='" + SqlEscape(r.second) + "' WHERE id=" + std::to_string(r.first));
                }
            }
        }
    }
    // 5) 唯一键
    {
        std::string has;
        QueryScalar("SELECT COUNT(*) FROM information_schema.STATISTICS "
                    "WHERE table_schema=DATABASE() AND table_name='generators' AND index_name='uq_generator_name'", has);
        if (has != "1" && !ExecSQL("ALTER TABLE generators ADD UNIQUE KEY uq_generator_name (name)", &err))
            return false;
    }
    // 6) problem_generators 删除 name 列（已备份到 generators.name）
    {
        std::string has;
        QueryScalar("SELECT COUNT(*) FROM information_schema.COLUMNS "
                    "WHERE table_schema=DATABASE() AND table_name='problem_generators' AND column_name='name'", has);
        if (has == "1" && !ExecSQL("ALTER TABLE problem_generators DROP COLUMN name", &err))
            return false;
    }
    return true;
}

} // namespace

namespace oj {

bool mysql_available() { return LoadMySql(); }

// 加密：AES-256-CBC → base64（库中存储密文）；解密反之。
std::string encrypt_blob(const std::string& plain) {
    std::string cip;
    if (!AesCrypt(true, plain, cip)) return "";
    return Base64Encode(cip);
}

std::string decrypt_blob(const std::string& enc) {
    if (enc.empty()) return "";
    std::string raw = Base64Decode(enc);
    std::string plain;
    if (!AesCrypt(false, raw, plain)) return "";
    return plain;
}

bool mysql_connect(const std::string& host, unsigned int port,
                   const std::string& user, const std::string& pass,
                   const std::string& db, std::string& err) {
    if (!LoadMySql()) { err = "libmysql.dll 未找到，请安装 MySQL Connector/C 并放到 exe 同目录"; return false; }
    g_conn = g_sql.mysql_init(nullptr);
    if (!g_conn) { err = "mysql_init failed"; return false; }
    g_conn = g_sql.mysql_real_connect(g_conn, host.c_str(), user.c_str(), pass.c_str(),
                                db.c_str(), port, nullptr, 0);
    if (!g_conn && !db.empty()) {
        // 目标库不存在时：先无库连接并自动建库，再重连目标库（首次在远程部署时免手工建库）
        void* c2 = g_sql.mysql_init(nullptr);
        c2 = g_sql.mysql_real_connect(c2, host.c_str(), user.c_str(), pass.c_str(),
                                      nullptr, port, nullptr, 0);
        if (c2) {
            std::string createDb = "CREATE DATABASE IF NOT EXISTS `" + db + "` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci";
            g_sql.mysql_query(c2, createDb.c_str());
            g_sql.mysql_close(c2);
            g_conn = g_sql.mysql_init(nullptr);
            g_conn = g_sql.mysql_real_connect(g_conn, host.c_str(), user.c_str(), pass.c_str(),
                                              db.c_str(), port, nullptr, 0);
        }
    }
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
  nickname VARCHAR(64) NOT NULL DEFAULT '',
  avatar MEDIUMTEXT,
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
  wrong_count INT NOT NULL DEFAULT 0,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  UNIQUE KEY uq_user_problem (user_id, problem_id, contest_id),
  FOREIGN KEY (user_id) REFERENCES users(id)
))SQL",
        R"SQL(CREATE TABLE IF NOT EXISTS contest_registrations (
  id INT AUTO_INCREMENT PRIMARY KEY,
  user_id INT NOT NULL,
  contest_id INT NOT NULL,
  is_virtual TINYINT NOT NULL DEFAULT 0,
  registered_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  UNIQUE KEY uq_user_contest (user_id, contest_id),
  FOREIGN KEY (user_id) REFERENCES users(id)
))SQL",
        R"SQL(CREATE TABLE IF NOT EXISTS problems (
  id INT PRIMARY KEY,
  title VARCHAR(255) NOT NULL DEFAULT '',
  description MEDIUMTEXT,
  sample_in MEDIUMTEXT,
  sample_out MEDIUMTEXT,
  time_ms INT NOT NULL DEFAULT 1000,
  mem_mb INT NOT NULL DEFAULT 256,
  tags TEXT,
  std_code MEDIUMTEXT,
  is_public TINYINT NOT NULL DEFAULT 1,
  updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
))SQL",
        R"SQL(CREATE TABLE IF NOT EXISTS generators (
  id INT AUTO_INCREMENT PRIMARY KEY,
  name VARCHAR(64) NOT NULL,
  code MEDIUMTEXT NOT NULL,
  description TEXT,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  UNIQUE KEY uq_generator_name (name)
))SQL",
        R"SQL(CREATE TABLE IF NOT EXISTS problem_generators (
  problem_id INT NOT NULL,
  generator_id INT NOT NULL,
  gen_count INT NOT NULL DEFAULT 0,
  seed_base INT NOT NULL DEFAULT 0,
  PRIMARY KEY (problem_id, generator_id),
  FOREIGN KEY (problem_id) REFERENCES problems(id),
  FOREIGN KEY (generator_id) REFERENCES generators(id)
))SQL",
        R"SQL(CREATE TABLE IF NOT EXISTS contests (
  id INT PRIMARY KEY,
  content MEDIUMTEXT NOT NULL
))SQL"
    };
    // 旧版 problem_generators（含 code/version 列的宽表）结构不同，先删除让新 DDL 重建
    {
        std::string hasOld;
        if (QueryScalar("SELECT COUNT(*) FROM information_schema.COLUMNS "
                        "WHERE table_schema=DATABASE() AND table_name='problem_generators' "
                        "AND column_name IN ('code','version')",
                        hasOld) && hasOld != "0") {
            ExecSQL("DROP TABLE IF EXISTS problem_generators");
        }
    }
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
    // 2.5) 错误次数列：wrong_count（ICPC 罚时：AC 前每次错误 +20 分钟）
    {
        std::string hasWrong;
        if (!QueryScalar("SELECT COUNT(*) FROM information_schema.COLUMNS "
                         "WHERE table_schema=DATABASE() AND table_name='submissions' AND column_name='wrong_count'",
                         hasWrong)) hasWrong = "0";
        if (hasWrong == "0" && !ExecSQL("ALTER TABLE submissions ADD COLUMN wrong_count INT NOT NULL DEFAULT 0", &err)) return false;
    }
    // 3) 用户资料列：nickname / avatar（老库升级，新库 DDL 已包含）
    {
        std::string hasNick;
        if (!QueryScalar("SELECT COUNT(*) FROM information_schema.COLUMNS "
                         "WHERE table_schema=DATABASE() AND table_name='users' AND column_name='nickname'",
                         hasNick)) hasNick = "0";
        if (hasNick == "0" && !ExecSQL("ALTER TABLE users ADD COLUMN nickname VARCHAR(64) NOT NULL DEFAULT ''", &err)) return false;

        std::string hasAvatar;
        if (!QueryScalar("SELECT COUNT(*) FROM information_schema.COLUMNS "
                         "WHERE table_schema=DATABASE() AND table_name='users' AND column_name='avatar'",
                         hasAvatar)) hasAvatar = "0";
        if (hasAvatar == "0" && !ExecSQL("ALTER TABLE users ADD COLUMN avatar MEDIUMTEXT", &err)) return false;
    }
    // 3.5) 题目标程源码列：std_code（生成器分发模式：客户端用它重新生成 .out）
    {
        std::string hasStd;
        if (!QueryScalar("SELECT COUNT(*) FROM information_schema.COLUMNS "
                         "WHERE table_schema=DATABASE() AND table_name='problems' AND column_name='std_code'",
                         hasStd)) hasStd = "0";
        if (hasStd == "0" && !ExecSQL("ALTER TABLE problems ADD COLUMN std_code MEDIUMTEXT", &err)) return false;
    }
    // 3.6) 题目公开状态列：is_public（1=公开，客户端可见；0=未公开，仅服务端可见）
    {
        std::string hasPub;
        if (!QueryScalar("SELECT COUNT(*) FROM information_schema.COLUMNS "
                         "WHERE table_schema=DATABASE() AND table_name='problems' AND column_name='is_public'",
                         hasPub)) hasPub = "0";
        if (hasPub == "0" && !ExecSQL("ALTER TABLE problems ADD COLUMN is_public TINYINT NOT NULL DEFAULT 1", &err)) return false;
    }
    // 4) 匿名默认用户（未登录提交统一归到该账号；空哈希无法登录）
    if (!ExecSQL("INSERT IGNORE INTO users(username,password_hash,salt,role) VALUES('anonymous','','','user')", &err)) return false;
    // 5) 生成器库（generators.name / problem_generators 去掉 name）
    if (!migrate_generator_schema(err)) return false;
    return true;
}

bool mysql_register(const std::string& username, const std::string& password,
                    const std::string& role, std::string& err) {
    if (username.size() < 2 || username.size() > 64) { err = "用户名长度 2~64"; return false; }
    if (password.size() < 6) { err = "密码至少 6 位"; return false; }
    std::string salt = RandomHex(16);
    std::string hash = Sha256Hex(salt + password);
    std::string sql = "INSERT INTO users(username,password_hash,salt,role,nickname) VALUES('"
        + SqlEscape(username) + "','" + hash + "','" + salt + "','"
        + (role == "admin" || role == "author" ? role : "user") + "','"
        + SqlEscape(username) + "')";
    if (!ExecSQL(sql, &err)) {
        if (g_sql.mysql_errno && g_sql.mysql_errno(g_conn) == 1062) err = "用户名已被注册";
        return false;
    }
    return true;
}

bool mysql_login(const std::string& username, const std::string& password,
                 std::string& out_token, long long& out_user_id,
                 std::string& out_role, std::string& out_username,
                 std::string& out_nickname, std::string& out_avatar, std::string& err) {
    std::string sql = "SELECT id, password_hash, salt, role, username, nickname, avatar FROM users WHERE username='"
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
    std::string nickname = row[5] ? row[5] : "";
    std::string avatar = row[6] ? row[6] : "";
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
    out_nickname = nickname;
    out_avatar = avatar;
    return true;
}

bool mysql_whoami(const std::string& token, long long& out_user_id,
                  std::string& out_role, std::string& out_username,
                  std::string& out_nickname, std::string& out_avatar, std::string& err) {
    std::string sql = "SELECT u.id, u.role, u.username, u.nickname, u.avatar FROM sessions s JOIN users u ON u.id=s.user_id "
                      "WHERE s.token='" + SqlEscape(token) + "' AND s.expires_at > NOW()";
    if (!ExecSQL(sql, &err)) return false;
    void* res = g_sql.mysql_store_result(g_conn);
    if (!res) return false;
    char** row = g_sql.mysql_fetch_row(res);
    if (!row) { g_sql.mysql_free_result(res); err = "会话无效或已过期"; return false; }
    out_user_id = atoll(row[0]);
    out_role = row[1] ? row[1] : "user";
    out_username = row[2] ? row[2] : "";
    out_nickname = row[3] ? row[3] : "";
    out_avatar = row[4] ? row[4] : "";
    g_sql.mysql_free_result(res);
    return true;
}

bool mysql_update_profile(long long user_id, const std::string& nickname,
                          const std::string& avatar, std::string& err) {
    std::string sql = "UPDATE users SET nickname='" + SqlEscape(nickname)
        + "', avatar='" + SqlEscape(avatar) + "' WHERE id=" + std::to_string(user_id);
    return ExecSQL(sql, &err);
}

bool mysql_logout(const std::string& token) {
    std::string sql = "DELETE FROM sessions WHERE token='" + SqlEscape(token) + "'";
    return ExecSQL(sql);
}

long long mysql_upsert_submission(long long user_id, int problem_id, int contest_id,
                                  const std::string& verdict, const std::string& detail,
                                  int time_ms, bool virt, const std::string& ts) {
    // ICPC 语义：
    //  · 已 AC 的题不再被后续提交改写（保持最早 AC 时间与罚时）；
    //  · 首次 AC 时 created_at = 本次时间（最早 AC 时间），wrong_count 保留累计错误次数；
    //  · AC 前每次错误提交 wrong_count+1（榜单罚时 +20 分钟/次）。
    // 返回该行 submissions.id，供 LSM 侧用同一 id 作为 submission:{id} 的键，
    // 保证提交列表（MySQL id）与双击详情（LSM submission:{id}）一一对应。
    int cid = contest_id > 0 ? contest_id : 0;
    bool ac = (verdict == "AC");

    // 读当前该用户该题（本场比赛）的提交状态（含行 id）
    std::string curVerdict;
    long long existingId = 0;
    bool exists = false;
    {
        std::string q = "SELECT id, verdict FROM submissions WHERE user_id="
            + std::to_string(user_id) + " AND problem_id=" + std::to_string(problem_id)
            + " AND contest_id=" + std::to_string(cid) + " LIMIT 1";
        if (g_conn && g_sql.mysql_query && g_sql.mysql_store_result && g_sql.mysql_fetch_row) {
            if (g_sql.mysql_query(g_conn, q.c_str()) == 0) {
                void* res = g_sql.mysql_store_result(g_conn);
                if (res) {
                    char** row = g_sql.mysql_fetch_row(res);
                    if (row) {
                        exists = true;
                        existingId = row[0] ? atoll(row[0]) : 0;
                        curVerdict = row[1] ? row[1] : "";
                    }
                    g_sql.mysql_free_result(res);
                }
            }
        }
    }

    // 已 AC：之后任何提交（对/错）都不再改动 MySQL，返回既有行 id
    if (exists && curVerdict == "AC") return existingId;

    if (!exists) {
        // 首条记录：wrong_count = AC?0:1
        long long wrong = ac ? 0 : 1;
        std::string sql = "INSERT INTO submissions(user_id,problem_id,contest_id,verdict,detail,time_ms,`virtual`,wrong_count,created_at) VALUES("
            + std::to_string(user_id) + "," + std::to_string(problem_id) + ","
            + std::to_string(cid) + ",'" + SqlEscape(verdict) + "','" + SqlEscape(detail)
            + "'," + std::to_string(time_ms) + "," + (virt ? "1" : "0") + ","
            + std::to_string(wrong) + ",'" + SqlEscape(ts) + "')";
        if (!ExecSQL(sql)) return 0;
        return g_sql.mysql_insert_id ? (long long)g_sql.mysql_insert_id(g_conn) : 0;
    }

    if (ac) {
        // 首次 AC：verdict=AC，created_at=本次时间（最早 AC 时间），wrong_count 保留累计错误次数
        std::string sql = "UPDATE submissions SET verdict='AC', detail='" + SqlEscape(detail)
            + "', time_ms=" + std::to_string(time_ms)
            + ", `virtual`=" + (virt ? "1" : "0")
            + ", created_at='" + SqlEscape(ts) + "'"
            + " WHERE user_id=" + std::to_string(user_id)
            + " AND problem_id=" + std::to_string(problem_id)
            + " AND contest_id=" + std::to_string(cid);
        if (!ExecSQL(sql)) return 0;
        return existingId;
    }

    // AC 前的错误提交：wrong_count+1，verdict 更新为最新错误判定（供进度显示）
    std::string sql = "UPDATE submissions SET verdict='" + SqlEscape(verdict)
        + "', detail='" + SqlEscape(detail) + "', time_ms=" + std::to_string(time_ms)
        + ", `virtual`=" + (virt ? "1" : "0")
        + ", wrong_count=wrong_count+1"
        + " WHERE user_id=" + std::to_string(user_id)
        + " AND problem_id=" + std::to_string(problem_id)
        + " AND contest_id=" + std::to_string(cid);
    if (!ExecSQL(sql)) return 0;
    return existingId;
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

bool mysql_contest_submissions(int cid, const std::string& username, bool view_all,
                               std::string& out_json) {
    // 每人每题每比赛只保留最后一次结果（submissions 唯一键 upsert 语义），时间倒序
    // view_all=false 时只返回 username 本人记录（比赛进行中参赛者互不可见）
    std::string sql =
        "SELECT s.id, s.problem_id, u.username, s.verdict, s.detail, s.time_ms, "
        "s.`virtual`, DATE_FORMAT(s.created_at, '%Y-%m-%d %H:%i:%s') "
        "FROM submissions s JOIN users u ON u.id=s.user_id "
        "WHERE s.contest_id=" + std::to_string(cid);
    if (!view_all)
        sql += " AND u.username='" + SqlEscape(username) + "'";
    sql += " ORDER BY s.created_at DESC, s.id DESC";
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

bool mysql_user_progress(const std::string& username, int contest_id, std::string& out_json) {
    long long uid = 0;
    if (!mysql_user_id_by_name(username, uid)) { out_json = "[]"; return true; }
    std::string sql = "SELECT problem_id, verdict FROM submissions WHERE user_id="
        + std::to_string(uid) + " AND contest_id=" + std::to_string(contest_id)
        + " ORDER BY problem_id";
    if (!ExecSQL(sql)) return false;
    void* res = g_sql.mysql_store_result(g_conn);
    if (!res) return false;
    out_json = "[";
    bool first = true;
    char** row;
    while ((row = g_sql.mysql_fetch_row(res)) != nullptr) {
        if (!first) out_json += ",";
        first = false;
        std::string v = row[1] ? row[1] : "";
        bool ac = (v == "AC");
        out_json += "{\"problemId\":" + std::string(row[0] ? row[0] : "0")
                 + ",\"verdict\":\"" + jsonEscRow(v) + "\""
                 + ",\"ac\":" + (ac ? "true" : "false") + "}";
    }
    g_sql.mysql_free_result(res);
    out_json += "]";
    return true;
}
bool mysql_board(int cid, const std::string& start_str,
                const std::string& problems_csv,
                std::string& out_official, std::string& out_virtual) {
    // 排行榜罚时逻辑（ICPC）：
    //   每题罚时 = 第一次 AC 相对基准时间（分钟） + wrong_count * 20 分钟；
    //   正式选手基准 = 比赛开始时间；虚拟选手基准 = 其报名时间（无报名则回退比赛开始）。
    std::string startEsc = SqlEscape(start_str);
    std::string sql =
        "SELECT u.username, s.`virtual`, s.wrong_count, "
        "  TIMESTAMPDIFF(MINUTE, IF(s.`virtual`=1, COALESCE(cr.registered_at, '" + startEsc + "'), '" + startEsc + "'), s.created_at) AS rel_min, "
        "  IF(s.verdict='AC', 1, 0) AS is_ac "
        "FROM submissions s JOIN users u ON u.id=s.user_id "
        "LEFT JOIN contest_registrations cr ON cr.user_id=s.user_id AND cr.contest_id=s.contest_id "
        "WHERE s.contest_id=" + std::to_string(cid) +
        " AND s.problem_id IN (" + problems_csv + ")";
    if (!ExecSQL(sql)) return false;
    void* res = g_sql.mysql_store_result(g_conn);
    if (!res) return false;

    std::map<std::string, long long> solved, penalty;
    std::map<std::string, bool> isVirt;
    char** row;
    while ((row = g_sql.mysql_fetch_row(res)) != nullptr) {
        std::string username = row[0] ? row[0] : "?";
        int virt = atoi(row[1]);
        long long wrong = atoll(row[2]);
        long long relMin = atoll(row[3]);
        bool ac = atoi(row[4]) != 0;
        if (!ac) continue;   // 未 AC 的题不计入榜单（ICPC 只统计已解决题目）
        solved[username] += 1;
        penalty[username] += relMin + wrong * 20;
        isVirt[username] = (virt == 1);
    }
    g_sql.mysql_free_result(res);

    auto emit = [&](bool virt) -> std::string {
        struct O { std::string name; long long s; long long p; };
        std::vector<O> v;
        for (auto& kv : solved) {
            if (isVirt[kv.first] != virt) continue;
            v.push_back({kv.first, kv.second, penalty[kv.first]});
        }
        std::sort(v.begin(), v.end(), [](const O& a, const O& b) {
            if (a.s != b.s) return a.s > b.s;    // AC 数降序
            return a.p < b.p;                    // 总罚时升序
        });
        std::string out = "[";
        for (size_t i = 0; i < v.size(); ++i) {
            if (i) out += ",";
            std::string name = v[i].name;
            for (auto& c : name) if (c == '"' || c == '\\') c = '_';
            out += "{\"rank\":" + std::to_string((int)i + 1)
                 + ",\"username\":\"" + name + "\""
                 + ",\"solved\":" + std::to_string(v[i].s)
                 + ",\"penalty\":" + std::to_string(v[i].p) + "}";
        }
        out += "]";
        return out;
    };
    out_official = emit(false);
    out_virtual = emit(true);
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

// ===== 题目 / 比赛发布与同步 =====

bool mysql_upsert_problem(int id, const std::string& title, const std::string& desc,
                          const std::string& sample_in, const std::string& sample_out,
                          int time_ms, int mem_mb, const std::string& tags_json,
                          const std::string& std_code, bool is_public, std::string& err) {
    if (time_ms <= 0) time_ms = 1000;
    if (mem_mb <= 0) mem_mb = 256;
    std::string tags = tags_json.empty() ? "[]" : tags_json;
    std::string encStd = std_code.empty() ? "" : encrypt_blob(std_code);
    std::string sql = "INSERT INTO problems(id,title,description,sample_in,sample_out,time_ms,mem_mb,tags,std_code,is_public,updated_at) VALUES("
        + std::to_string(id) + ",'" + SqlEscape(title) + "','" + SqlEscape(desc) + "','"
        + SqlEscape(sample_in) + "','" + SqlEscape(sample_out) + "'," + std::to_string(time_ms) + ","
        + std::to_string(mem_mb) + ",'" + SqlEscape(tags) + "','" + SqlEscape(encStd) + "',"
        + (is_public ? "1" : "0") + ",NOW()) "
        "ON DUPLICATE KEY UPDATE title=VALUES(title), description=VALUES(description), "
        "sample_in=VALUES(sample_in), sample_out=VALUES(sample_out), time_ms=VALUES(time_ms), "
        "mem_mb=VALUES(mem_mb), tags=VALUES(tags), std_code=VALUES(std_code), "
        "is_public=VALUES(is_public), updated_at=NOW()";
    return ExecSQL(sql, &err);
}

bool mysql_clear_problem_generators(int problem_id) {
    return ExecSQL("DELETE FROM problem_generators WHERE problem_id=" + std::to_string(problem_id));
}

long long mysql_add_problem_generator(int problem_id, const std::string& name,
                                      const std::string& code, const std::string& desc,
                                      int gen_count, int seed_base) {
    std::string encCode = encrypt_blob(code);
    std::string sql = "INSERT INTO generators(code,description) VALUES('"
        + SqlEscape(encCode) + "','" + SqlEscape(desc) + "')";
    if (!ExecSQL(sql)) return 0;
    long long gid = g_sql.mysql_insert_id ? (long long)g_sql.mysql_insert_id(g_conn) : 0;
    if (gid <= 0) return 0;
    std::string link = "INSERT INTO problem_generators(problem_id,generator_id,name,gen_count,seed_base) VALUES("
        + std::to_string(problem_id) + "," + std::to_string(gid) + ",'" + SqlEscape(name) + "',"
        + std::to_string(gen_count) + "," + std::to_string(seed_base) + ")";
    if (!ExecSQL(link)) return 0;
    return gid;
}

void mysql_purge_orphan_generators() {
    ExecSQL("DELETE FROM generators WHERE id NOT IN (SELECT generator_id FROM problem_generators)");
}

// ===== 生成器库（全局 generators 表：出题端增删改查 + 题↔生成器绑定） =====

bool mysql_list_generators(std::string& out_json) {
    if (!ExecSQL("SELECT id,name,description,created_at FROM generators ORDER BY name")) return false;
    void* res = g_sql.mysql_store_result(g_conn);
    if (!res) return false;
    out_json = "[";
    bool first = true;
    char** row;
    while ((row = g_sql.mysql_fetch_row(res)) != nullptr) {
        if (!first) out_json += ",";
        first = false;
        out_json += "{\"id\":" + std::string(row[0] ? row[0] : "0")
                 + ",\"name\":\"" + jsonEscRow(row[1] ? row[1] : "") + "\""
                 + ",\"description\":\"" + jsonEscRow(row[2] ? row[2] : "") + "\""
                 + ",\"createdAt\":\"" + (row[3] ? row[3] : "") + "\"}";
    }
    g_sql.mysql_free_result(res);
    out_json += "]";
    return true;
}

bool mysql_get_generator(int id, std::string& out_name, std::string& out_code, std::string& out_desc) {
    std::string sql = "SELECT name,code,description FROM generators WHERE id=" + std::to_string(id);
    if (!ExecSQL(sql)) return false;
    void* res = g_sql.mysql_store_result(g_conn);
    if (!res) return false;
    char** row = g_sql.mysql_fetch_row(res);
    if (!row) { g_sql.mysql_free_result(res); return false; }
    out_name = row[0] ? row[0] : "";
    out_code = decrypt_blob(row[1] ? row[1] : "");
    out_desc = row[2] ? row[2] : "";
    g_sql.mysql_free_result(res);
    return true;
}

bool mysql_create_generator(const std::string& name, const std::string& code, const std::string& desc,
                            long long& out_id, std::string& err) {
    std::string enc = encrypt_blob(code);
    std::string sql = "INSERT INTO generators(name,code,description) VALUES('"
        + SqlEscape(name) + "','" + SqlEscape(enc) + "','" + SqlEscape(desc) + "')";
    if (!ExecSQL(sql, &err)) {
        if (g_sql.mysql_errno && g_sql.mysql_errno(g_conn) == 1062) err = "生成器名字已存在";
        return false;
    }
    out_id = g_sql.mysql_insert_id ? (long long)g_sql.mysql_insert_id(g_conn) : 0;
    return out_id > 0;
}

bool mysql_update_generator(int id, const std::string& code, const std::string& desc, std::string& err) {
    std::string enc = encrypt_blob(code);
    std::string sql = "UPDATE generators SET code='" + SqlEscape(enc)
        + "', description='" + SqlEscape(desc) + "' WHERE id=" + std::to_string(id);
    return ExecSQL(sql, &err);
}

bool mysql_problem_generators(int problem_id, std::string& out_json) {
    std::string sql = "SELECT g.id, g.name, pg.gen_count FROM problem_generators pg "
                      "JOIN generators g ON g.id=pg.generator_id WHERE pg.problem_id="
                      + std::to_string(problem_id) + " ORDER BY g.name";
    if (!ExecSQL(sql)) return false;
    void* res = g_sql.mysql_store_result(g_conn);
    if (!res) return false;
    out_json = "[";
    bool first = true;
    char** row;
    while ((row = g_sql.mysql_fetch_row(res)) != nullptr) {
        if (!first) out_json += ",";
        first = false;
        out_json += "{\"id\":" + std::string(row[0] ? row[0] : "0")
                 + ",\"name\":\"" + jsonEscRow(row[1] ? row[1] : "") + "\""
                 + ",\"genCount\":" + std::string(row[2] ? row[2] : "0") + "}";
    }
    g_sql.mysql_free_result(res);
    out_json += "]";
    return true;
}

bool mysql_bind_generator(int problem_id, int generator_id, int gen_count, std::string& err) {
    if (gen_count <= 0) gen_count = 10;
    // 确定性种子基准：由题目编号与生成器名字哈希组合，保证所有客户端生成同一份数据
    std::string name;
    if (!QueryScalar("SELECT name FROM generators WHERE id=" + std::to_string(generator_id), name) || name.empty()) {
        err = "生成器不存在";
        return false;
    }
    unsigned long long gh = HashString(name);
    int seedBase = (int)(((unsigned long long)problem_id * 1000003ull + gh) & 0x7fffffff);
    std::string sql = "INSERT INTO problem_generators(problem_id,generator_id,gen_count,seed_base) VALUES("
        + std::to_string(problem_id) + "," + std::to_string(generator_id) + ","
        + std::to_string(gen_count) + "," + std::to_string(seedBase) + ") "
        "ON DUPLICATE KEY UPDATE gen_count=VALUES(gen_count), seed_base=VALUES(seed_base)";
    return ExecSQL(sql, &err);
}

bool mysql_unbind_generator(int problem_id, int generator_id) {
    return ExecSQL("DELETE FROM problem_generators WHERE problem_id=" + std::to_string(problem_id)
                   + " AND generator_id=" + std::to_string(generator_id));
}

bool mysql_search_generators(const std::string& keyword, std::string& out_json) {
    if (keyword.empty()) return false;
    std::string sql = "SELECT id,name,code FROM generators ORDER BY name";
    if (!ExecSQL(sql)) return false;
    void* res = g_sql.mysql_store_result(g_conn);
    if (!res) return false;
    std::string kw = keyword;
    for (char& c : kw) c = (char)tolower((unsigned char)c);
    out_json = "[";
    bool first = true;
    char** row;
    while ((row = g_sql.mysql_fetch_row(res)) != nullptr) {
        int id = atoi(row[0]);
        std::string name = row[1] ? row[1] : "";
        std::string code = decrypt_blob(row[2] ? row[2] : "");
        std::istringstream iss(code);
        std::string line;
        int lineNo = 0, n = 0;
        while (std::getline(iss, line) && n < 30) {
            ++lineNo;
            std::string ll = line;
            for (char& c : ll) c = (char)tolower((unsigned char)c);
            if (ll.find(kw) != std::string::npos) {
                std::string t = line;
                size_t a = t.find_first_not_of(" \t\r\n");
                if (a != std::string::npos) t = t.substr(a);
                size_t b = t.find_last_not_of(" \t\r\n");
                if (b != std::string::npos) t = t.substr(0, b + 1);
                if (!first) out_json += ",";
                first = false;
                out_json += "{\"id\":" + std::to_string(id)
                         + ",\"name\":\"" + jsonEscRow(name) + "\""
                         + ",\"lineNo\":" + std::to_string(lineNo)
                         + ",\"line\":\"" + jsonEscRow(t) + "\""
                         + ",\"keyword\":\"" + jsonEscRow(keyword) + "\"}";
                ++n;
            }
        }
    }
    g_sql.mysql_free_result(res);
    out_json += "]";
    return true;
}

bool mysql_problem_visibility(std::string& out_json) {
    std::string sql = "SELECT id, is_public FROM problems ORDER BY id";
    if (!ExecSQL(sql)) return false;
    void* res = g_sql.mysql_store_result(g_conn);
    if (!res) return false;
    out_json = "{";
    bool first = true;
    char** row;
    while ((row = g_sql.mysql_fetch_row(res)) != nullptr) {
        if (!first) out_json += ",";
        first = false;
        out_json += "\"" + std::string(row[0] ? row[0] : "0") + "\":"
                 + std::string(atoi(row[1]) ? "true" : "false");
    }
    g_sql.mysql_free_result(res);
    out_json += "}";
    return true;
}

bool mysql_upsert_contest(int cid, const std::string& contest_json, std::string& err) {
    std::string sql = "INSERT INTO contests(id,content) VALUES("
        + std::to_string(cid) + ",'" + SqlEscape(contest_json) + "') "
        "ON DUPLICATE KEY UPDATE content=VALUES(content)";
    return ExecSQL(sql, &err);
}

// 解析 contest.json 中的 "problems":[1,2,3] 数组，返回题目编号列表
static std::vector<int> parseContestProblems(const std::string& json) {
    std::vector<int> ids;
    std::string mark = "\"problems\"";
    size_t p = json.find(mark);
    if (p == std::string::npos) return ids;
    p = json.find('[', p + mark.size());
    if (p == std::string::npos) return ids;
    size_t e = json.find(']', p);
    if (e == std::string::npos) return ids;
    std::string arr = json.substr(p + 1, e - p - 1);
    std::istringstream iss(arr);
    std::string tok;
    while (std::getline(iss, tok, ',')) {
        size_t a = tok.find_first_not_of(" \t\r\n");
        if (a == std::string::npos) continue;
        size_t b = tok.find_last_not_of(" \t\r\n");
        tok = tok.substr(a, b - a + 1);
        if (tok.empty()) continue;
        int id = atoi(tok.c_str());
        if (id > 0) ids.push_back(id);
    }
    return ids;
}

bool mysql_sync_problems(const std::string& problem_dir, const std::string& server_root, std::string& err) {
    if (!g_conn) { err = "MySQL not connected"; return false; }
    if (problem_dir.empty()) { err = "题目目录为空"; return false; }
    CreateDirectoryA(problem_dir.c_str(), nullptr);

    // 1) 题目（含标程源码 std.cpp）：公开题 + 被比赛引用的未公开题（比赛内客户端可见）
    std::set<int> contestPids;
    if (g_sql.mysql_query(g_conn, "SELECT content FROM contests") == 0) {
        void* cres = g_sql.mysql_store_result(g_conn);
        if (cres) {
            char** crow;
            while ((crow = g_sql.mysql_fetch_row(cres)) != nullptr) {
                if (crow[0]) {
                    auto ids = parseContestProblems(crow[0]);
                    contestPids.insert(ids.begin(), ids.end());
                }
            }
            g_sql.mysql_free_result(cres);
        }
    }
    std::string probSql = "SELECT id,title,description,sample_in,sample_out,time_ms,mem_mb,tags,std_code "
                          "FROM problems WHERE is_public=1";
    if (!contestPids.empty()) {
        probSql += " OR id IN (";
        bool firstPid = true;
        for (int pid : contestPids) {
            if (!firstPid) probSql += ",";
            firstPid = false;
            probSql += std::to_string(pid);
        }
        probSql += ")";
    }
    probSql += " ORDER BY id";
    if (g_sql.mysql_query(g_conn, probSql.c_str())) {
        if (g_sql.mysql_error) err = g_sql.mysql_error(g_conn);
        return false;
    }
    void* res = g_sql.mysql_store_result(g_conn);
    if (res) {
        char** row;
        while ((row = g_sql.mysql_fetch_row(res)) != nullptr) {
            int id = atoi(row[0]);
            std::string title = row[1] ? row[1] : "";
            std::string desc = row[2] ? row[2] : "";
            std::string sampleIn = row[3] ? row[3] : "";
            std::string sampleOut = row[4] ? row[4] : "";
            int timeMs = atoi(row[5]); if (timeMs <= 0) timeMs = 1000;
            int memMb = atoi(row[6]); if (memMb <= 0) memMb = 256;
            std::string tags = row[7] ? row[7] : "[]";
            std::string stdCode = decrypt_blob(row[8] ? row[8] : "");
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

            // 读取该题全部生成器（JOIN generators），解密后落盘 gen.cpp / desc.txt
            std::string gq = "SELECT g.name, pg.gen_count, pg.seed_base, g.code, g.description "
                             "FROM problem_generators pg JOIN generators g ON g.id=pg.generator_id "
                             "WHERE pg.problem_id=" + std::to_string(id) + " ORDER BY g.name";
            std::vector<GenInfo> allGens, regenGens;
            if (g_sql.mysql_query(g_conn, gq.c_str()) == 0) {
                void* gres = g_sql.mysql_store_result(g_conn);
                if (gres) {
                    char** grow;
                    while ((grow = g_sql.mysql_fetch_row(gres)) != nullptr) {
                        GenInfo gi;
                        gi.name = grow[0] ? grow[0] : "";
                        gi.count = atoi(grow[1]);
                        gi.seedBase = atoi(grow[2]);
                        std::string code = decrypt_blob(grow[3] ? grow[3] : "");
                        std::string gdesc = grow[4] ? grow[4] : "";
                        if (gi.name.empty() || gi.count <= 0) continue;

                        std::string gdir = dir + "\\" + gi.name;
                        Mkdirs(gdir);
                        WriteFileBin(gdir + "\\gen.cpp", code);
                        WriteFileBin(gdir + "\\desc.txt", gdesc);

                        // 变更检测：gen.cpp / 组数 / 种子 / 标程 任一变化则重新生成
                        std::string hashInput = code + "|" + std::to_string(gi.count)
                            + "|" + std::to_string(gi.seedBase) + "|" + stdCode;
                        gi.hash = ToHex(HashString(hashInput));
                        gi.marker = gdir + "\\.genhash";
                        bool complete = ExistsFile(gdir + "\\" + std::to_string(gi.count) + ".in")
                                     && ExistsFile(gdir + "\\" + std::to_string(gi.count) + ".out");
                        allGens.push_back(gi);
                        if (ReadFileText(gi.marker) != gi.hash || !complete)
                            regenGens.push_back(gi);

                    }
                    g_sql.mysql_free_result(gres);
                }
            }

            // 重新生成需要更新的生成器数据（非致命：失败仅记录，不中断其余同步）
            if (!regenGens.empty() && !stdCode.empty()) {
                std::string rerr;
                if (RegenerateProblemData(dir, regenGens, rerr)) {
                    for (auto& gi : regenGens) WriteFileBin(gi.marker, gi.hash);
                } else {
                    if (err.empty()) err = "题目 " + std::to_string(id) + " 数据生成失败：" + rerr;
                }
            }

            // 清理已删除的生成器目录（DB 中不存在的旧 gen.cpp 目录）
            {
                std::string pat = dir + "\\*";
                WIN32_FIND_DATAA fd; HANDLE h = FindFirstFileA(pat.c_str(), &fd);
                if (h != INVALID_HANDLE_VALUE) {
                    do {
                        if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) continue;
                        std::string n = fd.cFileName;
                        if (n == "." || n == "..") continue;
                        bool known = false;
                        for (auto& gi : allGens) if (gi.name == n) { known = true; break; }
                        if (!known && ExistsFile(dir + "\\" + n + "\\gen.cpp"))
                            RemoveDir(dir + "\\" + n);
                    } while (FindNextFileA(h, &fd));
                    FindClose(h);
                }
            }
        }
        g_sql.mysql_free_result(res);
    }

    // 2) 比赛
    if (!server_root.empty() && g_sql.mysql_query(g_conn, "SELECT id,content FROM contests ORDER BY id") == 0) {
        void* cres = g_sql.mysql_store_result(g_conn);
        if (cres) {
            char** row;
            while ((row = g_sql.mysql_fetch_row(cres)) != nullptr) {
                int cid = atoi(row[0]);
                std::string content = row[1] ? row[1] : "";
                if (content.empty()) continue;
                std::string cdir = server_root + "\\contests\\" + std::to_string(cid);
                Mkdirs(cdir);
                WriteFileBin(cdir + "\\contest.json", content);
            }
            g_sql.mysql_free_result(cres);
        }
    }
    err.clear();
    return true;
}

} // namespace oj
