// resp_client.h — LSM 访问客户端（经中间层 HTTP 透传，不再直连）
#pragma once

#include <mutex>
#include <optional>
#include <string>
#include <vector>

namespace oj {

class RespClient {
public:
    RespClient() = default;
    ~RespClient() = default;

    RespClient(const RespClient&) = delete;
    RespClient& operator=(const RespClient&) = delete;

    // 中间层模式下无需真实连接，恒返回 true。
    bool connect(const std::string& host, int port);
    void disconnect();
    bool is_connected() const;

    // 基础 KV
    bool set(const std::string& key, const std::string& value);
    std::optional<std::string> get(const std::string& key);   // nullopt = 不存在/失败
    long long del(const std::string& key);
    long long incr(const std::string& key);

    // 哈希
    bool hset(const std::string& key, const std::string& field, const std::string& value);
    std::optional<std::string> hget(const std::string& key, const std::string& field);
    std::vector<std::string> hkeys(const std::string& key);

private:
    mutable std::mutex mtx_;
    bool connected_ = false;
};

} // namespace oj
