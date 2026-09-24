// http_client.h — HTTP 客户端封装（基于 cpp-httplib）
#pragma once

#include "httplib.h"

#include <string>

namespace oj {

struct HttpResp {
    int status = 0;
    std::string body;
};

// 发起 HTTP 请求（GET/POST，支持 http/https）。token 非空时带 Authorization: Bearer。
inline bool http_request(const std::string& method, const std::string& baseUrl,
                         const std::string& pathQuery, const std::string& body,
                         const std::string& token, HttpResp& out, std::string& err) {
    httplib::Client cli(baseUrl);
    cli.set_connection_timeout(8, 0);
    cli.set_read_timeout(8, 0);
    cli.set_write_timeout(8, 0);
#ifdef CPPHTTPLIB_OPENSSL_SUPPORT
    // 服务端为自签证书，跳过证书校验
    cli.enable_server_certificate_verification(false);
#endif

    httplib::Headers headers;
    if (!token.empty()) headers.emplace("Authorization", "Bearer " + token);

    httplib::Result res;
    if (method == "POST")
        res = cli.Post(pathQuery, headers, body, "application/json");
    else
        res = cli.Get(pathQuery, headers);

    if (!res) {
        err = httplib::to_string(res.error());
        return false;
    }
    out.status = res->status;
    out.body = res->body;
    return true;
}

} // namespace oj
