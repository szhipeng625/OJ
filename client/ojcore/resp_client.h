// resp_client.h — 极简 RESP（Redis 序列化协议）客户端
// 用于与远端部署的 LSM 存储服务（默认端口 6379）通信，替代原先进程内嵌的 tiny-lsm 引擎。
// 仅实现 ojcore 需要的命令：SET / GET / DEL / INCR / HSET / HGET / HKEYS。
#pragma once

#include <chrono>
#include <cstdint>
#include <mutex>
#include <optional>
#include <string>
#include <vector>

namespace oj {

class RespClient {
public:
    RespClient() = default;
    ~RespClient();

    RespClient(const RespClient&) = delete;
    RespClient& operator=(const RespClient&) = delete;

    // 连接远端（失败返回 false）。后续命令在连接断开时会自动重连一次。
    bool connect(const std::string& host, int port);
    void disconnect();
    bool is_connected() const;

    // 基础 KV
    bool set(const std::string& key, const std::string& value);
    std::optional<std::string> get(const std::string& key);  // nullopt = 不存在/失败
    long long del(const std::string& key);                   // 返回删除个数
    long long incr(const std::string& key);                  // 返回自增后的值，失败返回 0

    // 哈希
    bool hset(const std::string& key, const std::string& field, const std::string& value);
    std::optional<std::string> hget(const std::string& key, const std::string& field);
    std::vector<std::string> hkeys(const std::string& key);

private:
    struct Reply {
        enum Type { Nil, Simple, Error, Integer, Bulk, Array } type = Nil;
        std::string str;               // Simple / Error / Bulk 的载荷
        long long integer = 0;         // Integer
        std::vector<Reply> array;      // Array
    };

    bool request(const std::vector<std::string>& args, Reply& out);

    bool connectLockedUnsafe();      // 调用方需持有 mtx_
    void disconnectLockedUnsafe();   // 调用方需持有 mtx_
    bool sendAll(const char* data, size_t len);
    bool recvAll(char* data, size_t len);
    bool readLine(std::string& line);
    bool readReply(Reply& out);

    mutable std::mutex mtx_;
    uintptr_t sock_ = kInvalidSocket;
    std::string host_;
    int port_ = 6379;
    bool connected_ = false;
    std::chrono::steady_clock::time_point failUntil_{};   // 连接失败后的退避截止时间

    static constexpr uintptr_t kInvalidSocket = ~0ull;
};

} // namespace oj
