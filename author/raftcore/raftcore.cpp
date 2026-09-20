// raftcore.cpp — 轻量 Raft 共识引擎实现
// 选举：随机选举超时(150-300ms)，term 递增，多数票即 Leader
// 复制：Leader 心跳携带日志，AppendEntries 校验 prevLog，多数 matchIndex 即提交
// 持久化：term / votedFor / log 落盘（文本格式，原子替换）
// 传输：Win32 TCP 短连接（每 RPC 一次 connect），文本行协议 + 二进制 payload
#include "raftcore.h"
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <mutex>
#include <random>
#include <string>
#include <thread>
#include <vector>

#pragma comment(lib, "ws2_32.lib")

namespace {

struct Entry {
    long long term;
    std::string payload;
};

struct PeerState {
    std::string addr;
    long long nextIndex = 1;
    long long matchIndex = 0;
    long long lastFailMs = 0; // 最近一次通信失败时间（steady ms），用于限速重试
};

// Raft 节点状态机
class RaftNode {
public:
    RaftConfig cfg;
    RaftApplyFn applyFn = nullptr;
    void* applyUser = nullptr;

    std::mutex mu;
    int role = 0;                       // 0 follower 1 candidate 2 leader
    int term = 0;
    int votedFor = -1;
    std::vector<Entry> log;             // log[0] 为哨兵（term 0，空）
    long long commitIndex = 0;
    long long lastApplied = 0;
    std::vector<PeerState> peers;
    std::atomic<bool> running{false};
    std::atomic<bool> stopFlag{false};
    std::thread listenTh;
    std::thread timerTh;
    SOCKET listenSock = INVALID_SOCKET;
    std::mt19937 rng;
    int electionTimeoutMs = 200;
    std::chrono::steady_clock::time_point lastHeartbeat; // 收到合法 leader 心跳的时间
    int leaderId = -1;
    // 选举
    int votesGranted = 0;
    std::atomic<long long> proposeIdx{-1}; // 最近一次 propose 的日志 index

    RaftNode() {
        std::random_device rd;
        rng.seed(rd());
        log.push_back(Entry{0, ""}); // 哨兵条目，index 0
    }

    void resetElectionTimeout() {
        std::uniform_int_distribution<int> d(150, 300);
        electionTimeoutMs = d(rng);
        lastHeartbeat = std::chrono::steady_clock::now();

    }

    // ---------- 持久化 ----------
    std::string statePath() const { return std::string(cfg.persist_dir) + "\\raft.state"; }

    void persist() {
        std::string tmp = statePath() + ".tmp";
        {
            std::ofstream f(tmp, std::ios::binary | std::ios::trunc);
            f << term << "\n" << votedFor << "\n" << (log.size() - 1) << "\n";
            for (size_t i = 1; i < log.size(); ++i) {
                f << log[i].term << "\n" << log[i].payload.size() << "\n";
                f.write(log[i].payload.data(), (std::streamsize)log[i].payload.size());
                f << "\n";
            }
            f.flush();
        }
        MoveFileExA(tmp.c_str(), statePath().c_str(), MOVEFILE_REPLACE_EXISTING);
    }

    void load() {
        std::ifstream f(statePath(), std::ios::binary);
        if (!f) return;
        int logCount = 0;
        f >> term >> votedFor >> logCount;
        log.clear();
        log.push_back(Entry{0, ""}); // 哨兵条目
        for (int i = 0; i < logCount; ++i) {
            Entry e;
            long long len = 0;
            f >> e.term >> len;
            f.get(); // consume '\n'
            std::string payload(len, '\0');
            if (len > 0) f.read(&payload[0], len);
            f.get(); // consume '\n'
            e.payload = std::move(payload);
            log.push_back(std::move(e));
        }
    }

    // ---------- 日志 ----------
    long long lastLogIndex() const { return (long long)log.size() - 1; }
    long long lastLogTerm() const { return log.back().term; }
    long long termAt(long long idx) const {
        if (idx < 0 || idx >= (long long)log.size()) return -1;
        return log[idx].term;
    }

    // ---------- RPC 收发 ----------
    // connect 带超时：非阻塞 connect + select（Windows connect 到未监听端口会挂起数秒）
    static bool connectTimeout(SOCKET s, const sockaddr_in* sa, int timeout_ms) {
        u_long mode = 1;
        ioctlsocket(s, FIONBIO, &mode);
        int rc = connect(s, (const sockaddr*)sa, sizeof(sockaddr_in));
        if (rc == 0) { mode = 0; ioctlsocket(s, FIONBIO, &mode); return true; }
        if (WSAGetLastError() != WSAEWOULDBLOCK) return false;
        fd_set w;
        FD_ZERO(&w);
        FD_SET(s, &w);
        timeval tv;
        tv.tv_sec = timeout_ms / 1000;
        tv.tv_usec = (timeout_ms % 1000) * 1000;
        rc = select(0, nullptr, &w, nullptr, &tv);
        if (rc <= 0) return false;
        int err = 0;
        int elen = sizeof(err);
        getsockopt(s, SOL_SOCKET, SO_ERROR, (char*)&err, &elen);
        if (err != 0) return false;
        mode = 0;
        ioctlsocket(s, FIONBIO, &mode);
        return true;
    }

    static bool parseAddr(const std::string& addr, std::string& ip, int& port) {
        size_t c = addr.find(':');
        if (c == std::string::npos) return false;
        ip = addr.substr(0, c);
        port = atoi(addr.substr(c + 1).c_str());
        return port > 0;
    }

    static bool sendAll(SOCKET s, const char* data, int len) {
        int sent = 0;
        while (sent < len) {
            int n = send(s, data + sent, len - sent, 0);
            if (n <= 0) return false;
            sent += n;
        }
        return true;
    }

    static bool recvExact(SOCKET s, char* buf, int len) {
        int got = 0;
        while (got < len) {
            int n = recv(s, buf + got, len - got, 0);
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }

    static bool recvLine(SOCKET s, std::string& line) {
        line.clear();
        char c;
        while (true) {
            int n = recv(s, &c, 1, 0);
            if (n <= 0) return false;
            if (c == '\n') return true;
            line += c;
        }
    }

    // 发送请求并读响应行。req 不含换行；payload 跟随（可为空）
    std::string rpc(const std::string& addr, const std::string& req,
                    const std::vector<std::string>& payloads) {
        std::string ip;
        int port = 0;
        if (!parseAddr(addr, ip, port)) return "";
        SOCKET s = socket(AF_INET, SOCK_STREAM, 0);
        if (s == INVALID_SOCKET) return "";
        DWORD tv = 800;
        setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, (const char*)&tv, sizeof(tv));
        sockaddr_in sa{};
        sa.sin_family = AF_INET;
        sa.sin_port = htons((u_short)port);
        inet_pton(AF_INET, ip.c_str(), &sa.sin_addr);
        if (!connectTimeout(s, &sa, 120)) { closesocket(s); return ""; }

        std::string msg = req + "\n";
        for (auto& p : payloads) {
            // 每条目已编码：[4B term][4B len][payload]，长度自校验
            uint32_t eterm = 0;
            uint32_t len = 0;
            if (p.size() >= 8) {
                eterm = ((uint32_t)(unsigned char)p[0] << 24) | ((uint32_t)(unsigned char)p[1] << 16) |
                        ((uint32_t)(unsigned char)p[2] << 8) | (uint32_t)(unsigned char)p[3];
                len = ((uint32_t)(unsigned char)p[4] << 24) | ((uint32_t)(unsigned char)p[5] << 16) |
                      ((uint32_t)(unsigned char)p[6] << 8) | (uint32_t)(unsigned char)p[7];
            }
            msg.append(p.data(), p.size());
            if (8 + len != p.size()) { closesocket(s); return ""; }
        }
        if (!sendAll(s, msg.data(), (int)msg.size())) { closesocket(s); return ""; }
        std::string resp;
        if (!recvLine(s, resp)) { closesocket(s); return ""; }
        closesocket(s);
        return resp;
    }

    std::string rpcOne(const std::string& addr, const std::string& req) {
        std::vector<std::string> empty;
        return rpc(addr, req, empty);
    }

    // ---------- 请求处理（listener 线程调用） ----------
    void handleConn(SOCKET s) {
        std::string line;
        if (!recvLine(s, line)) { closesocket(s); return; }

        std::vector<std::string> parts;
        size_t p = 0;
        while (p <= line.size()) {
            size_t sp = line.find(' ', p);
            if (sp == std::string::npos) sp = line.size();
            parts.push_back(line.substr(p, sp - p));
            p = sp + 1;
        }

        std::string resp;
        if (parts.empty()) { closesocket(s); return; }

        if (parts[0] == "VOTE") {
            // VOTE term cand lastIdx lastTerm
            int rTerm = atoi(parts[1].c_str());
            int cand = atoi(parts[2].c_str());
            long long lastIdx = atoll(parts[3].c_str());
            long long lastTerm = atoll(parts[4].c_str());
            std::lock_guard<std::mutex> lk(mu);
            bool grant = false;
            if (rTerm > term) { term = rTerm; role = 0; votedFor = -1; resetElectionTimeout(); persist(); }
            if (rTerm == term &&
                (votedFor == -1 || votedFor == cand) &&
                (lastTerm > lastLogTerm() ||
                 (lastTerm == lastLogTerm() && lastIdx >= lastLogIndex()))) {
                grant = true;
                votedFor = cand;
                role = 0;
                resetElectionTimeout();
                persist();
            }
            char buf[128];
            snprintf(buf, sizeof(buf), "VOTED %d %d", term, grant ? 1 : 0);
            resp = buf;
        } else if (parts[0] == "APPEND") {
            // APPEND term leaderId prevIdx prevTerm leaderCommit n [entries]
            int rTerm = atoi(parts[1].c_str());
            int lead = atoi(parts[2].c_str());
            long long prevIdx = atoll(parts[3].c_str());
            long long prevTerm = atoll(parts[4].c_str());
            long long lCommit = atoll(parts[5].c_str());
            int n = atoi(parts[6].c_str());

            // 读取 entries（每条：[4B term][4B len][payload]）
            std::vector<Entry> entries;
            for (int i = 0; i < n; ++i) {
                char tb[4], lb[4];
                if (!recvExact(s, tb, 4) || !recvExact(s, lb, 4)) { closesocket(s); return; }
                uint32_t eterm = ((uint32_t)(unsigned char)tb[0] << 24) | ((uint32_t)(unsigned char)tb[1] << 16) |
                                 ((uint32_t)(unsigned char)tb[2] << 8) | (uint32_t)(unsigned char)tb[3];
                uint32_t len = ((uint32_t)(unsigned char)lb[0] << 24) | ((uint32_t)(unsigned char)lb[1] << 16) |
                               ((uint32_t)(unsigned char)lb[2] << 8) | (uint32_t)(unsigned char)lb[3];
                if (len > 256u * 1024 * 1024) { closesocket(s); return; } // 防异常
                std::string payload(len, '\0');
                if (len > 0 && !recvExact(s, &payload[0], (int)len)) { closesocket(s); return; }
                entries.push_back(Entry{(long long)eterm, std::move(payload)});
            }

            std::lock_guard<std::mutex> lk(mu);
            bool success = false;
            long long matchIdx = 0;
            if (rTerm >= term) {
                if (rTerm > term) { term = rTerm; role = 0; votedFor = -1; resetElectionTimeout(); persist(); }
                leaderId = lead;
                role = 0;
                resetElectionTimeout();
                // 校验 prevLog
                if (prevIdx <= lastLogIndex() && termAt(prevIdx) == prevTerm) {
                    // 冲突截断
                    if (log.size() > (size_t)(prevIdx + 1)) {
                        log.resize((size_t)(prevIdx + 1));
                    }
                    for (auto& e : entries) log.push_back(std::move(e));
                    if (!entries.empty()) persist();
                    matchIdx = lastLogIndex();
                    success = true;
                }
                // 推进 commitIndex
                if (lCommit > commitIndex) {
                    commitIndex = (std::min)(lCommit, lastLogIndex());
                }
                applyCommitted();
            }
            char buf[128];
            snprintf(buf, sizeof(buf), "APPR %d %d %lld", term, success ? 1 : 0, matchIdx);
            resp = buf;
        } else {
            closesocket(s);
            return;
        }
        sendAll(s, resp.c_str(), (int)resp.size());
        sendAll(s, "\n", 1);
        closesocket(s);
    }

    // 应用已提交条目（调用方持锁）
    void applyCommitted() {
        while (lastApplied < commitIndex) {
            lastApplied++;
            if (lastApplied < (long long)log.size()) {
                auto& e = log[(size_t)lastApplied];
                if (applyFn) applyFn(e.payload.data(), (int)e.payload.size(), applyUser);
            }
        }
    }

    // ---------- 选举与心跳 ----------
    void startElection() {
        std::unique_lock<std::mutex> lk(mu);
        term++;

        role = 1;
        votedFor = cfg.node_id;
        votesGranted = 1;
        persist();
        long long lastIdx = lastLogIndex();
        long long lastTerm = lastLogTerm();
        int myTerm = term;
        int myId = cfg.node_id;
        char req[256];
        snprintf(req, sizeof(req), "VOTE %d %d %lld %lld", myTerm, myId, lastIdx, lastTerm);
        std::vector<std::string> addrs;
        for (auto& p : peers) addrs.push_back(p.addr);
        lk.unlock();

        int grants = 1;
        for (auto& a : addrs) {
            std::string resp = rpcOne(a, req);
            int rTerm = 0, granted = 0;
            if (sscanf(resp.c_str(), "VOTED %d %d", &rTerm, &granted) == 2 && granted) {
                std::lock_guard<std::mutex> lk2(mu);
                if (rTerm > term) { term = rTerm; role = 0; votedFor = -1; resetElectionTimeout(); persist(); return; }
                if (role == 1 && rTerm == term) {
                    grants++;
                    if (grants > (int)(peers.size() + 1) / 2) {
                        becomeLeader();
                        return;
                    }
                }
            }
        }
    }

    void becomeLeader() {

        role = 2;
        leaderId = cfg.node_id;
        for (auto& p : peers) { p.nextIndex = lastLogIndex() + 1; p.matchIndex = 0; }
        char buf[128];
        snprintf(buf, sizeof(buf), "NEWLEADER %d %d", cfg.node_id, term);
        // 广播一次空 APPEND 确立 leader
    }

    void sendHeartbeat() {
        std::unique_lock<std::mutex> lk(mu);
        if (role != 2) return;
        int myTerm = term;
        int myId = cfg.node_id;
        long long leaderCommit = commitIndex;
        long long lastIdx = lastLogIndex();
        std::vector<PeerState> snap = peers;
        lk.unlock();

        for (size_t i = 0; i < snap.size(); ++i) {
            std::unique_lock<std::mutex> lk2(mu);
            if (role != 2) return;
            long long prevIdx = snap[i].nextIndex - 1;
            long long prevTerm = termAt(prevIdx);
            long long nextIdx = snap[i].nextIndex;
            std::vector<std::string> chunks;
            long long cnt = 0;
            if (nextIdx <= lastIdx) {
                // 批量发，一次最多 64 条；每条编码 [4B term][4B len][payload]
                for (long long idx = nextIdx; idx <= lastIdx && cnt < 64; ++idx) {
                    auto& e = log[(size_t)idx];
                    std::string chunk;
                    uint32_t eterm = (uint32_t)e.term;
                    uint32_t len = (uint32_t)e.payload.size();
                    char h[8];
                    h[0] = (char)(eterm >> 24); h[1] = (char)(eterm >> 16);
                    h[2] = (char)(eterm >> 8);  h[3] = (char)eterm;
                    h[4] = (char)(len >> 24);   h[5] = (char)(len >> 16);
                    h[6] = (char)(len >> 8);    h[7] = (char)len;
                    chunk.append(h, 8);
                    chunk += e.payload;
                    chunks.push_back(std::move(chunk));
                    cnt++;
                }
            }
            std::string addr = snap[i].addr;
            char req[512];
            snprintf(req, sizeof(req), "APPEND %d %d %lld %lld %lld %lld",
                     myTerm, myId, prevIdx, prevTerm, leaderCommit, cnt);
            lk2.unlock();

            std::string resp = rpc(addr, req, chunks);
            int rTerm = 0, success = 0;
            long long matchIdx = 0;
            if (sscanf(resp.c_str(), "APPR %d %d %lld", &rTerm, &success, &matchIdx) == 3) {
                std::lock_guard<std::mutex> lk3(mu);
                peers[i].lastFailMs = 0;
                if (rTerm > term) { term = rTerm; role = 0; votedFor = -1; resetElectionTimeout(); persist(); return; }
                if (role == 2 && rTerm == term && success) {
                    peers[i].matchIndex = matchIdx;
                    peers[i].nextIndex = matchIdx + 1;
                    advanceCommit();
                } else if (role == 2 && rTerm == term && !success) {
                    if (peers[i].nextIndex > 1) peers[i].nextIndex--;
                }
            }
        }
    }

    // 计算多数 matchIndex 推进 commit（调用方持锁）
    void advanceCommit() {
        if (peers.empty()) {
            // 单节点：直接提交到最新
            commitIndex = lastLogIndex();
            applyCommitted();
            return;
        }
        // 取 matchIndex 集合的中位数（含自己=lastLogIndex）
        std::vector<long long> ms;
        ms.push_back(lastLogIndex());
        for (auto& p : peers) ms.push_back(p.matchIndex);
        std::sort(ms.begin(), ms.end());
        long long m = ms[ms.size() / 2]; // 多数位（ceil）
        if (m > commitIndex && termAt(m) == term) {
            commitIndex = m;
            applyCommitted();
        }
    }

    long long propose(const std::string& payload) {
        std::lock_guard<std::mutex> lk(mu);
        if (role != 2) return -1;
        Entry e{(long long)term, payload};
        log.push_back(std::move(e));
        persist();
        long long idx = lastLogIndex();
        if (peers.empty()) {
            commitIndex = idx;
            applyCommitted();
        }
        return idx;
    }
};

RaftNode* g_node = nullptr;

} // namespace

// ---------- 监听线程 ----------
static DWORD WINAPI listenerProc(LPVOID) {
    while (!g_node->stopFlag.load()) {
        SOCKET s = accept(g_node->listenSock, nullptr, nullptr);
        if (s == INVALID_SOCKET) { Sleep(10); continue; }
        g_node->handleConn(s);
    }
    return 0;
}

// ---------- 定时器线程：选举 / 心跳 ----------
static void timerProc() {
    using namespace std::chrono;
    auto lastHeartbeat = steady_clock::now();
    while (!g_node->stopFlag.load()) {
        Sleep(25);
        auto now = steady_clock::now();

        int role;
        {
            std::lock_guard<std::mutex> lk(g_node->mu);
            role = g_node->role;
        }
        if (role == 2) {
            // Leader：每 100ms 心跳
            if (duration_cast<milliseconds>(now - lastHeartbeat).count() >= 100) {
                g_node->sendHeartbeat();
                lastHeartbeat = now;
            }
        } else {
            // Follower/Candidate：选举超时检测
            bool doElection = false;
            {
                std::lock_guard<std::mutex> lk(g_node->mu);
                long long hbAge = duration_cast<milliseconds>(now - g_node->lastHeartbeat).count();
                if (hbAge >= g_node->electionTimeoutMs) {
                        if (g_node->peers.empty()) {
                        // 单节点：直接 Leader
                        if (g_node->role == 0) {
                            g_node->term++;
                            g_node->role = 2;
                            g_node->leaderId = g_node->cfg.node_id;
                            g_node->votedFor = g_node->cfg.node_id;
                            g_node->persist();
                            g_node->resetElectionTimeout();
                        }
                    } else {
                        doElection = true; // 锁外启动选举，避免重入死锁
                    }
                }
            }
            if (doElection) g_node->startElection();
        }
    }
}

// ---------- 导出函数 ----------
extern "C" {

RK_API int rk_init(const RaftConfig* cfg, RaftApplyFn apply, void* user) {
    if (g_node) return -1;
    WSADATA wsa;
    WSAStartup(MAKEWORD(2, 2), &wsa);

    g_node = new RaftNode();
    g_node->cfg = *cfg;
    g_node->cfg.listen_addr = cfg->listen_addr;
    g_node->cfg.persist_dir = cfg->persist_dir;
    g_node->applyFn = apply;
    g_node->applyUser = user;

    // 持久化目录
    std::string dir = cfg->persist_dir ? cfg->persist_dir : ".";
    CreateDirectoryA(dir.c_str(), NULL);
    g_node->cfg.persist_dir = strdup(dir.c_str());

    // peers
    for (int i = 0; i < cfg->peer_count; ++i) {
        PeerState ps;
        ps.addr = cfg->peers[i];
        g_node->peers.push_back(ps);
    }

    g_node->load();
    g_node->running.store(true);
    g_node->stopFlag.store(false);
    g_node->resetElectionTimeout();

    // listener
    std::string ip;
    int port = 0;
    if (!RaftNode::parseAddr(cfg->listen_addr, ip, port)) { delete g_node; g_node = nullptr; return -2; }
    g_node->listenSock = socket(AF_INET, SOCK_STREAM, 0);
    if (g_node->listenSock == INVALID_SOCKET) { delete g_node; g_node = nullptr; return -3; }
    sockaddr_in sa{};
    sa.sin_family = AF_INET;
    sa.sin_port = htons((u_short)port);
    inet_pton(AF_INET, ip.c_str(), &sa.sin_addr);
    if (bind(g_node->listenSock, (sockaddr*)&sa, sizeof(sa)) != 0 ||
        listen(g_node->listenSock, 16) != 0) {
        closesocket(g_node->listenSock);
        delete g_node; g_node = nullptr;
        return -4;
    }
    CreateThread(nullptr, 0, listenerProc, nullptr, 0, nullptr);
    g_node->timerTh = std::thread(timerProc);
    return 0;
}

RK_API long long rk_propose(const char* payload, int len) {
    if (!g_node) return -1;
    return g_node->propose(std::string(payload, len));
}

RK_API int rk_wait_commit(long long index, int timeout_ms) {
    if (!g_node) return -2;
    int waited = 0;
    while (waited < timeout_ms) {
        {
            std::lock_guard<std::mutex> lk(g_node->mu);
            if (g_node->role != 2) return -2;
            if (g_node->commitIndex >= index) return 0;
        }
        Sleep(10);
        waited += 10;
    }
    return -1;
}

RK_API int rk_is_leader(void) {
    if (!g_node) return 0;
    std::lock_guard<std::mutex> lk(g_node->mu);
    return g_node->role == 2 ? 1 : 0;
}

RK_API int rk_leader_id(void) {
    if (!g_node) return -1;
    std::lock_guard<std::mutex> lk(g_node->mu);
    return g_node->leaderId;
}

RK_API int rk_current_term(void) {
    if (!g_node) return 0;
    std::lock_guard<std::mutex> lk(g_node->mu);
    return g_node->term;
}

RK_API long long rk_commit_index(void) {
    if (!g_node) return 0;
    std::lock_guard<std::mutex> lk(g_node->mu);
    return g_node->commitIndex;
}

RK_API long long rk_log_size(void) {
    if (!g_node) return 0;
    std::lock_guard<std::mutex> lk(g_node->mu);
    return (long long)g_node->log.size() - 1;
}

RK_API int rk_shutdown(void) {
    if (!g_node) return -1;
    g_node->stopFlag.store(true);
    if (g_node->timerTh.joinable()) g_node->timerTh.join();
    if (g_node->listenSock != INVALID_SOCKET) {
        closesocket(g_node->listenSock);
        g_node->listenSock = INVALID_SOCKET;
    }
    delete g_node;
    g_node = nullptr;
    WSACleanup();
    return 0;
}

} // extern "C"

#define NOMINMAX
