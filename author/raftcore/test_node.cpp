// test_node.cpp — Raft 集群节点测试程序（常驻 + 命令文件驱动）
// 用法:
//   test_node <id> <listen> <peers_csv> <state_dir> <apply_dir> [--put <payload>] [--solo]
// 常驻模式：轮询 state_dir\cmd.txt，支持命令：
//   "PUT:<payload>"  提交日志条目（等 commit 后写 result.txt）
//   "STATUS"         写当前状态到 result.txt
//   "EXIT"           退出进程
// 结果写入 state_dir\result.txt
#include "raftcore.h"
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>
#include <windows.h>

static int g_seq = 0;
static void applyFn(const char* payload, int len, void* user) {
    const char* dir = (const char*)user;
    char path[1024];
    snprintf(path, sizeof(path), "%s\\applied_%03d.txt", dir, g_seq++);
    FILE* f = fopen(path, "wb");
    if (f) { fwrite(payload, 1, len, f); fclose(f); }
    printf("APPLY seq=%03d len=%d -> %s\n", g_seq - 1, len, path);
    fflush(stdout);
}

static bool readFile(const std::string& path, std::string& out) {
    FILE* f = fopen(path.c_str(), "rb");
    if (!f) return false;
    fseek(f, 0, SEEK_END);
    long sz = ftell(f);
    fseek(f, 0, SEEK_SET);
    out.assign(sz > 0 ? sz : 0, '\0');
    if (sz > 0) fread(&out[0], 1, sz, f);
    fclose(f);
    return true;
}

static void writeFile(const std::string& path, const std::string& data) {
    FILE* f = fopen(path.c_str(), "wb");
    if (f) { fwrite(data.data(), 1, data.size(), f); fclose(f); }
}

int main(int argc, char** argv) {
    if (argc < 6) { printf("usage: test_node <id> <listen> <peers_csv> <state_dir> <apply_dir> [--put <payload>]\n"); return 2; }
    int id = atoi(argv[1]);
    std::string listen = argv[2];
    std::string peers_csv = argv[3]; if (peers_csv == "--") peers_csv = "";
    std::string state_dir = argv[4];
    std::string apply_dir = argv[5];
    std::string put_payload;
    for (int i = 6; i < argc; ++i) {
        if (strcmp(argv[i], "--put") == 0 && i + 1 < argc) put_payload = argv[++i];
    }

    std::vector<std::string> peer_vec;
    size_t p = 0;
    while (p <= peers_csv.size()) {
        size_t c = peers_csv.find(',', p);
        if (c == std::string::npos) c = peers_csv.size();
        std::string a = peers_csv.substr(p, c - p);
        if (!a.empty()) peer_vec.push_back(a);
        p = c + 1;
    }
    std::vector<const char*> peers;
    for (auto& a : peer_vec) peers.push_back(a.c_str());

    CreateDirectoryA(state_dir.c_str(), NULL);
    CreateDirectoryA(apply_dir.c_str(), NULL);

    RaftConfig cfg;
    cfg.node_id = id;
    cfg.listen_addr = listen.c_str();
    cfg.peers = peers.empty() ? nullptr : peers.data();
    cfg.peer_count = (int)peers.size();
    cfg.persist_dir = state_dir.c_str();

    int rc = rk_init(&cfg, applyFn, (void*)apply_dir.c_str());
    if (rc != 0) { printf("INIT-FAIL rc=%d\n", rc); return 1; }
    printf("READY id=%d listen=%s peers=%d\n", id, listen.c_str(), cfg.peer_count);
    fflush(stdout);

    std::string cmdPath = state_dir + "\\cmd.txt";
    std::string resPath = state_dir + "\\result.txt";
    DeleteFileA(cmdPath.c_str());
    DeleteFileA(resPath.c_str());

    // 若带 --put：等成为 leader 后提交一次（用于单节点/独立验证）
    if (!put_payload.empty()) {
        for (int i = 0; i < 100; ++i) {
            if (rk_is_leader()) break;
            Sleep(100);
        }
        if (!rk_is_leader()) {
            printf("NOT-LEADER id=%d leaderId=%d\n", id, rk_leader_id());
            return 1;
        }
        printf("LEADER id=%d term=%d\n", id, rk_current_term());
        long long idx = rk_propose(put_payload.c_str(), (int)put_payload.size());
        int wrc = rk_wait_commit(idx, 15000);
        printf("PUT idx=%lld rc=%d commit=%lld\n", idx, wrc, rk_commit_index());
        Sleep(800);
        rk_shutdown();
        return wrc == 0 ? 0 : 1;
    }

    // 常驻模式：命令文件驱动
    printf("CMD-LOOP id=%d\n", id);
    fflush(stdout);
    while (true) {
        std::string cmd;
        if (readFile(cmdPath, cmd) && !cmd.empty()) {
            // trim trailing CRLF (Set-Content appends CRLF)
            while (!cmd.empty() && (cmd.back() == 13 || cmd.back() == 10)) cmd.pop_back();
            DeleteFileA(cmdPath.c_str());
            printf("CMD id=%d: %s\n", id, cmd.c_str());
            fflush(stdout);
            if (cmd == "EXIT") break;
            if (cmd == "STATUS") {
                char buf[256];
                snprintf(buf, sizeof(buf), "STATUS id=%d isLeader=%d leaderId=%d term=%d commit=%lld log=%lld",
                         id, rk_is_leader(), rk_leader_id(), rk_current_term(), rk_commit_index(), rk_log_size());
                writeFile(resPath, buf);
                continue;
            }
            if (cmd.rfind("PUT:", 0) == 0) {
                std::string payload = cmd.substr(4);
                long long idx = rk_propose(payload.c_str(), (int)payload.size());
                if (idx < 0) {
                    writeFile(resPath, "ERR NOT-LEADER");
                } else {
                    int wrc = rk_wait_commit(idx, 15000);
                    char buf[128];
                    snprintf(buf, sizeof(buf), "PUT-OK idx=%lld rc=%d commit=%lld isLeader=%d", idx, wrc, rk_commit_index(), rk_is_leader());
                    writeFile(resPath, buf);
                }
                continue;
            }
        }
        Sleep(100);
    }
    printf("EXIT id=%d\n", id);
    fflush(stdout);
    rk_shutdown();
    return 0;
}
