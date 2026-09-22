// resp_client.cpp — RESP 客户端实现（Winsock2，阻塞式）
#include "resp_client.h"

#include <winsock2.h>
#include <ws2tcpip.h>

#include <cstdlib>
#include <cstring>

namespace oj {

namespace {

// Winsock 初始化（进程生命周期内仅一次）
struct WsaGuard {
    WsaGuard() { WSADATA d = {}; WSAStartup(MAKEWORD(2, 2), &d); }
    ~WsaGuard() { WSACleanup(); }
};
WsaGuard g_wsa;

// 把参数编码成 RESP 命令帧：*<n>\r\n 后跟每个参数的 $<len>\r\n<arg>\r\n
std::string encodeCommand(const std::vector<std::string>& args) {
    std::string out;
    out.reserve(64 + args.size() * 16);
    out += "*" + std::to_string(args.size()) + "\r\n";
    for (const auto& a : args) {
        out += "$" + std::to_string(a.size()) + "\r\n" + a + "\r\n";
    }
    return out;
}

} // namespace

RespClient::~RespClient() {
    disconnect();
}

bool RespClient::connect(const std::string& host, int port) {
    std::lock_guard<std::mutex> lock(mtx_);
    host_ = host.empty() ? "127.0.0.1" : host;
    port_ = (port > 0) ? port : 6379;
    return connectLockedUnsafe();
}

void RespClient::disconnect() {
    std::lock_guard<std::mutex> lock(mtx_);
    disconnectLockedUnsafe();
}

bool RespClient::is_connected() const {
    std::lock_guard<std::mutex> lock(mtx_);
    return connected_;
}

// 调用方需持有 mtx_。用 host_/port_ 建立连接并设置超时。
bool RespClient::connectLockedUnsafe() {
    disconnectLockedUnsafe();

    // 连接失败后退避 10 秒：期间不重复尝试，避免每次操作都阻塞 3 秒超时
    if (std::chrono::steady_clock::now() < failUntil_) return false;
    failUntil_ = std::chrono::steady_clock::now() + std::chrono::seconds(10);

    SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (s == INVALID_SOCKET) return false;

    sockaddr_in addr = {};
    addr.sin_family = AF_INET;
    addr.sin_port = htons(static_cast<u_short>(port_));

    if (inet_pton(AF_INET, host_.c_str(), &addr.sin_addr) != 1) {
        // 非点分十进制，按主机名解析
        addrinfo hints = {}, *res = nullptr;
        hints.ai_family = AF_INET;
        hints.ai_socktype = SOCK_STREAM;
        if (getaddrinfo(host_.c_str(), nullptr, &hints, &res) != 0 || !res) {
            closesocket(s);
            return false;
        }
        memcpy(&addr.sin_addr,
               &reinterpret_cast<sockaddr_in*>(res->ai_addr)->sin_addr,
               sizeof(addr.sin_addr));
        freeaddrinfo(res);
    }

    // 非阻塞 connect + select，3 秒超时
    u_long mode = 1;
    ioctlsocket(s, FIONBIO, &mode);
    ::connect(s, reinterpret_cast<sockaddr*>(&addr), sizeof(addr));
    fd_set wset, eset;
    FD_ZERO(&wset); FD_SET(s, &wset);
    FD_ZERO(&eset); FD_SET(s, &eset);
    timeval tv = {3, 0};
    int sel = select(0, nullptr, &wset, &eset, &tv);
    if (sel <= 0 || FD_ISSET(s, &eset)) {
        closesocket(s);
        return false;
    }
    int err = 0; int elen = sizeof(err);
    getsockopt(s, SOL_SOCKET, SO_ERROR, reinterpret_cast<char*>(&err), &elen);
    if (err != 0) {
        closesocket(s);
        return false;
    }
    mode = 0;
    ioctlsocket(s, FIONBIO, &mode);

    // 收/发超时 3 秒，避免远端异常时永久阻塞
    int tmo = 3000;
    setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&tmo), sizeof(tmo));
    setsockopt(s, SOL_SOCKET, SO_SNDTIMEO, reinterpret_cast<const char*>(&tmo), sizeof(tmo));

    sock_ = static_cast<uintptr_t>(s);
    connected_ = true;
    failUntil_ = {};   // 连接成功，清除退避
    return true;
}

// 调用方需持有 mtx_。
void RespClient::disconnectLockedUnsafe() {
    if (sock_ != kInvalidSocket) {
        closesocket(static_cast<SOCKET>(sock_));
    }
    sock_ = kInvalidSocket;
    connected_ = false;
}

bool RespClient::sendAll(const char* data, size_t len) {
    SOCKET s = static_cast<SOCKET>(sock_);
    size_t off = 0;
    while (off < len) {
        int n = ::send(s, data + off, static_cast<int>(len - off), 0);
        if (n <= 0) return false;
        off += static_cast<size_t>(n);
    }
    return true;
}

bool RespClient::recvAll(char* data, size_t len) {
    SOCKET s = static_cast<SOCKET>(sock_);
    size_t off = 0;
    while (off < len) {
        int n = ::recv(s, data + off, static_cast<int>(len - off), 0);
        if (n <= 0) return false;
        off += static_cast<size_t>(n);
    }
    return true;
}

bool RespClient::readLine(std::string& line) {
    line.clear();
    char c;
    while (true) {
        if (!recvAll(&c, 1)) return false;
        if (c == '\r') {
            if (!recvAll(&c, 1)) return false;
            if (c == '\n') return true;
            line += '\r';
        }
        line += c;
    }
}

bool RespClient::readReply(Reply& out) {
    std::string line;
    if (!readLine(line) || line.empty()) return false;
    char type = line[0];
    std::string rest = line.substr(1);
    switch (type) {
        case '+':
            out = Reply(); out.type = Reply::Simple; out.str = rest;
            return true;
        case '-':
            out = Reply(); out.type = Reply::Error; out.str = rest;
            return true;
        case ':':
            out = Reply(); out.type = Reply::Integer; out.integer = atoll(rest.c_str());
            return true;
        case '$': {
            long long len = atoll(rest.c_str());
            if (len < 0) { out = Reply(); out.type = Reply::Nil; return true; }
            std::string data;
            data.resize(static_cast<size_t>(len));
            if (len > 0 && !recvAll(&data[0], static_cast<size_t>(len))) return false;
            char crlf[2];
            if (!recvAll(crlf, 2)) return false;
            out = Reply(); out.type = Reply::Bulk; out.str = data;
            return true;
        }
        case '*': {
            long long n = atoll(rest.c_str());
            if (n < 0) { out = Reply(); out.type = Reply::Nil; return true; }
            out = Reply(); out.type = Reply::Array;
            out.array.reserve(static_cast<size_t>(n));
            for (long long i = 0; i < n; ++i) {
                Reply r;
                if (!readReply(r)) return false;
                out.array.push_back(std::move(r));
            }
            return true;
        }
        default:
            return false;
    }
}

bool RespClient::request(const std::vector<std::string>& args, Reply& out) {
    std::lock_guard<std::mutex> lock(mtx_);
    for (int attempt = 0; attempt < 2; ++attempt) {
        if (sock_ == kInvalidSocket) {
            if (host_.empty() || !connectLockedUnsafe()) return false;
        }
        std::string frame = encodeCommand(args);
        if (sendAll(frame.data(), frame.size()) && readReply(out)) {
            return true;
        }
        disconnectLockedUnsafe();  // 断开，下次循环重连后重试一次
    }
    return false;
}

bool RespClient::set(const std::string& key, const std::string& value) {
    Reply r;
    if (!request({"SET", key, value}, r)) return false;
    return r.type == Reply::Simple || r.type == Reply::Integer;
}

std::optional<std::string> RespClient::get(const std::string& key) {
    Reply r;
    if (!request({"GET", key}, r)) return std::nullopt;
    if (r.type == Reply::Bulk) return r.str;
    return std::nullopt;
}

long long RespClient::del(const std::string& key) {
    Reply r;
    if (!request({"DEL", key}, r)) return 0;
    return (r.type == Reply::Integer) ? r.integer : 0;
}

long long RespClient::incr(const std::string& key) {
    Reply r;
    if (!request({"INCR", key}, r)) return 0;
    return (r.type == Reply::Integer) ? r.integer : 0;
}

bool RespClient::hset(const std::string& key, const std::string& field, const std::string& value) {
    Reply r;
    if (!request({"HSET", key, field, value}, r)) return false;
    return r.type == Reply::Simple || r.type == Reply::Integer;
}

std::optional<std::string> RespClient::hget(const std::string& key, const std::string& field) {
    Reply r;
    if (!request({"HGET", key, field}, r)) return std::nullopt;
    if (r.type == Reply::Bulk) return r.str;
    return std::nullopt;
}

std::vector<std::string> RespClient::hkeys(const std::string& key) {
    Reply r;
    if (!request({"HKEYS", key}, r)) return {};
    std::vector<std::string> out;
    if (r.type != Reply::Array) return out;
    out.reserve(r.array.size());
    for (auto& e : r.array) {
        if (e.type == Reply::Bulk) out.push_back(std::move(e.str));
    }
    return out;
}

} // namespace oj
