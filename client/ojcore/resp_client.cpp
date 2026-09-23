// resp_client.cpp — LSM 访问客户端实现（经中间层 HTTP 透传，不再直连）
#include "resp_client.h"
#include "mysql_dao.h"
#include "http_client.h"
#include "json.hpp"

#include <mutex>

namespace oj {

using json = nlohmann::json;

bool RespClient::connect(const std::string& host, int port) {
    (void)host;
    (void)port;
    std::lock_guard<std::mutex> lock(mtx_);
    connected_ = true;
    return true;
}

void RespClient::disconnect() {
    std::lock_guard<std::mutex> lock(mtx_);
    connected_ = false;
}

bool RespClient::is_connected() const {
    std::lock_guard<std::mutex> lock(mtx_);
    return connected_;
}

// 统一 POST 到中间层 LSM 接口，返回解析后的 json（失败/非 ok 返回 nullopt）
static std::optional<json> lsm_call(const std::string& path, const json& payload) {
    std::string err;
    HttpResp r;
    if (!http_request("POST", middleware_url(), path, payload.dump(), middleware_token(), r, err))
        return std::nullopt;
    try {
        json j = json::parse(r.body);
        if (!j.value("ok", false)) return std::nullopt;
        return j;
    } catch (...) {
        return std::nullopt;
    }
}

static std::string getStr(const json& j, const char* key) {
    auto it = j.find(key);
    return (it != j.end() && it->is_string()) ? it->get<std::string>() : std::string();
}
static long long getInt(const json& j, const char* key) {
    auto it = j.find(key);
    return (it != j.end() && it->is_number()) ? it->get<long long>() : 0;
}
static bool getBool(const json& j, const char* key) {
    auto it = j.find(key);
    return (it != j.end() && it->is_boolean()) ? it->get<bool>() : false;
}

bool RespClient::set(const std::string& key, const std::string& value) {
    return lsm_call("/api/lsm/set", {{"key", key}, {"value", value}}).has_value();
}

std::optional<std::string> RespClient::get(const std::string& key) {
    auto j = lsm_call("/api/lsm/get", {{"key", key}});
    if (!j || !getBool(*j, "found")) return std::nullopt;
    return getStr(*j, "value");
}

long long RespClient::del(const std::string& key) {
    auto j = lsm_call("/api/lsm/del", {{"key", key}});
    return j ? getInt(*j, "count") : 0;
}

long long RespClient::incr(const std::string& key) {
    auto j = lsm_call("/api/lsm/incr", {{"key", key}});
    return j ? getInt(*j, "value") : 0;
}

bool RespClient::hset(const std::string& key, const std::string& field, const std::string& value) {
    return lsm_call("/api/lsm/hset", {{"key", key}, {"field", field}, {"value", value}}).has_value();
}

std::optional<std::string> RespClient::hget(const std::string& key, const std::string& field) {
    auto j = lsm_call("/api/lsm/hget", {{"key", key}, {"field", field}});
    if (!j || !getBool(*j, "found")) return std::nullopt;
    return getStr(*j, "value");
}

std::vector<std::string> RespClient::hkeys(const std::string& key) {
    auto j = lsm_call("/api/lsm/hkeys", {{"key", key}});
    if (!j) return {};
    std::vector<std::string> out;
    for (auto& e : j->value("keys", json::array()))
        if (e.is_string()) out.push_back(e.get<std::string>());
    return out;
}

} // namespace oj
