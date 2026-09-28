// crypt.h — 本地题目/比赛缓存文件的 AES-256-CBC 加解密（OpenSSL EVP）。
// 仅用于本地静态混淆（拉取到本地的题目数据加密落盘），密钥为固定常量，
// 与云端数据库里的 AES 密钥无关。
#pragma once

#include <fstream>
#include <sstream>
#include <string>

#include <openssl/evp.h>

namespace oj {

// 固定密钥 / IV（32 字节 key + 16 字节 iv），仅本地缓存用
static const unsigned char kLocalKey[32] = {
    0x4f, 0x6a, 0x2d, 0x6c, 0x6f, 0x63, 0x61, 0x6c,   // "Oj-local"
    0x2d, 0x63, 0x61, 0x63, 0x68, 0x65, 0x2d, 0x31,   // "-cache-1"
    0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39,   // "123456789"
    0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x30, 0x31,   // "ABCDEF01"
};
static const unsigned char kLocalIv[16] = {
    0x6f, 0x6a, 0x2d, 0x6c, 0x6f, 0x63, 0x61, 0x6c,   // "oj-local"
    0x2d, 0x69, 0x76, 0x2d, 0x31, 0x32, 0x33, 0x34,   // "-iv-1234"
};

// 加密后带 4 字节魔数前缀 "OJE1"，读取时据此区分密文与历史明文。
static const char* kMagic = "OJE1";

inline std::string encrypt_local(const std::string& plain) {
    if (plain.empty()) return std::string(kMagic);

    EVP_CIPHER_CTX* ctx = EVP_CIPHER_CTX_new();
    if (!ctx) return std::string(kMagic);
    std::string out;
    out.resize(plain.size() + 32);
    int len = 0, total = 0;
    bool ok = true;
    if (EVP_EncryptInit_ex(ctx, EVP_aes_256_cbc(), nullptr, kLocalKey, kLocalIv) != 1) ok = false;
    if (ok && EVP_EncryptUpdate(ctx, (unsigned char*)&out[0], &len,
                                (const unsigned char*)plain.data(), (int)plain.size()) != 1) ok = false;
    total = len;
    if (ok && EVP_EncryptFinal_ex(ctx, (unsigned char*)&out[0] + total, &len) != 1) ok = false;
    total += len;
    EVP_CIPHER_CTX_free(ctx);
    if (!ok) return std::string(kMagic);
    out.resize(total);
    return std::string(kMagic) + out;
}

inline std::string decrypt_local(const std::string& data) {
    // 非本格式（历史明文）直接返回原文
    if (data.size() < 4 || data.compare(0, 4, kMagic) != 0) return data;
    std::string cipher = data.substr(4);
    if (cipher.empty()) return std::string();

    EVP_CIPHER_CTX* ctx = EVP_CIPHER_CTX_new();
    if (!ctx) return data;
    std::string out;
    out.resize(cipher.size() + 32);
    int len = 0, total = 0;
    bool ok = true;
    if (EVP_DecryptInit_ex(ctx, EVP_aes_256_cbc(), nullptr, kLocalKey, kLocalIv) != 1) ok = false;
    if (ok && EVP_DecryptUpdate(ctx, (unsigned char*)&out[0], &len,
                                (const unsigned char*)cipher.data(), (int)cipher.size()) != 1) ok = false;
    total = len;
    if (ok && EVP_DecryptFinal_ex(ctx, (unsigned char*)&out[0] + total, &len) != 1) ok = false;
    total += len;
    EVP_CIPHER_CTX_free(ctx);
    if (!ok) return data;
    out.resize(total);
    return out;
}

inline std::string read_enc_file(const std::string& path) {
    std::ifstream f(path, std::ios::binary);
    if (!f) return std::string();
    std::stringstream ss;
    ss << f.rdbuf();
    return decrypt_local(ss.str());
}

inline void write_enc_file(const std::string& path, const std::string& content) {
    std::string enc = encrypt_local(content);
    std::ofstream f(path, std::ios::binary | std::ios::trunc);
    if (f) f.write(enc.data(), (std::streamsize)enc.size());
}

} // namespace oj
