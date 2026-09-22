# LSM 服务器运维操作记录

> 服务器：43.157.39.171 (lhins-awjq88yx, eu-frankfurt)
> 时间：2026-09-22
> 操作人：OrcaTerm AI + 用户

---

## 一、问题背景

LSM 服务（类 Redis 的 LSM-Tree 存储引擎）部署在腾讯云 Lighthouse 实例上，对外提供 RESP 协议接口。客户端通过公网 IP 访问时遇到以下问题：

1. 公网无法连接 LSM 服务（端口 6379/6380）
2. 大 payload（如 submission JSON）的 SET 命令失败
3. INCR 命令导致服务进程崩溃
4. MemTable 递归锁可能导致死锁

---

## 二、排查过程

### 2.1 公网连通性排查

**现象**：MySQL 3306 端口公网可通，但 LSM 6379/6380 端口不通。

**排查步骤**：

```bash
# 1. 确认 LSM 进程和端口
ps aux | grep lsm_server
ss -tlnp | grep 6379

# 2. 本地回环测试 → 正常
nc -w 3 127.0.0.1 6379 <<< $'*1\r\n$4\r\nPING\r\n'
# 返回: +PONG

# 3. 内网 IP 测试 → 正常
nc -w 3 10.9.0.6 6379 <<< $'*1\r\n$4\r\nPING\r\n'
# 返回: +PONG

# 4. 公网 IP 测试 → 超时
nc -w 5 43.157.39.171 6379 <<< $'*1\r\n$4\r\nPING\r\n'
# 返回: (无响应，TCP 握手超时)
```

**结论**：问题不在 LSM 服务本身，而在腾讯云网络层。

**尝试的修复**：
- 删除并重建云防火墙 6379 规则 → 无效
- 切换端口到 6380 → 无效
- 实例自检发现安全组限制了非标准端口

**最终方案**：需在腾讯云控制台「安全组」中放行对应端口（防火墙和安全组是两层独立的访问控制）。

### 2.2 端口切换（6379 → 6380）

修改 `server/src/server.cpp`：

```cpp
// 修改前
RedisServer server(io_context, 6379);

// 修改后
RedisServer server(io_context, 6380);
```

重新编译并重启：

```bash
cd ~/lsm/lsm/build && make -j$(nproc) lsm_server
cd ~/lsm/lsm && nohup ./build/lsm_server > /tmp/lsm.log 2>&1 &
```

---

## 三、Bug 修复

### Bug 1：do_read 大请求截断

**文件**：`server/src/server.cpp`

**问题**：`do_read` 只调用一次 `async_read_until` 读取第一行（`*N\r\n`），就假设剩余 `N*2` 行已在缓冲区中。当 payload 跨多个 TCP 段时，缓冲区数据不完整，导致解析失败返回 `-ERR partial request received`。

**修复**：改为递归读取所有行后再解析。新增 `read_line` 和 `read_rest` 辅助函数：

```cpp
// 从 buffer_ 取一行（去掉行尾 \r\n）
bool read_line(std::string& out) {
    std::istream is(&buffer_);
    std::string line;
    if (!std::getline(is, line)) return false;
    if (!line.empty() && line.back() == '\r') line.pop_back();
    out = std::move(line);
    return true;
}

// 递归读取剩余行
void read_rest(int total_lines, int done, std::string full_request) {
    auto self(shared_from_this());
    if (done >= total_lines) {
        do_write(handleRequest(full_request));
        return;
    }
    asio::async_read_until(socket_, buffer_, "\r\n",
        [this, self, total_lines, done, full_request]
        (const asio::error_code& ec, std::size_t) mutable {
            if (ec) { /* error handling */ return; }
            std::string line;
            if (!read_line(line)) { /* error */ return; }
            full_request += line + "\r\n";
            read_rest(total_lines, done + 1, full_request);
        });
}

void do_read() {
    auto self(shared_from_this());
    asio::async_read_until(socket_, buffer_, "\r\n",
        [this, self](const asio::error_code& ec, std::size_t) {
            // ... 读取第一行，解析 numElements ...
            read_rest(numElements * 2, 0, first + "\r\n");
        });
}
```

**验证**：

```bash
# 发送 1500 字节的大 payload
VAL=$(python3 -c "print('x'*1500)")
printf '*3\r\n$3\r\nSET\r\n$9\r\nbig_test\r\n$%d\r\n%s\r\n' ${#VAL} "$VAL" | nc 127.0.0.1 6380
# 返回: +OK

# 读取验证
echo -e '*2\r\n$3\r\nGET\r\n$9\r\nbig_test\r\n' | nc 127.0.0.1 6380
# 返回: $1500\r\nxxxxx... (正确返回 1500 字节)
```

### Bug 2：redis_incr 异常穿透导致进程崩溃

**文件**：`src/redis_wrapper/redis_wrapper.cpp`

**问题**：`redis_incr` 中 `std::stoll(original_vale.value())` 不捕获异常。当 key 的值不是合法数字时，异常穿透 asio 回调，触发 `std::terminate`，进程退出（退出码 134 = SIGABRT）。

**修复**：给 `std::stoll` 加上 try/catch：

```cpp
// 修改前
auto new_value = std::to_string(std::stoll(original_vale.value()) + 1);

// 修改后
int64_t incr_val = 0;
try {
    incr_val = std::stoll(original_vale.value()) + 1;
} catch (const std::exception &) {
    incr_val = 1;
}
auto new_value = std::to_string(incr_val);
```

### Bug 3：MemTable get_total_size 递归锁

**文件**：`src/memtable/memtable.cpp`

**问题**：`get_total_size()` 先对 `cur_mtx` 和 `frozen_mtx` 加读锁，然后调用 `get_frozen_size()` 和 `get_cur_size()`，这两个函数内部又分别加读锁。`std::shared_mutex` 不支持递归加锁，有写者等待时会导致死锁或未定义行为。

**修复**：持锁后直接访问成员变量，不调用会再次加锁的子函数：

```cpp
// 修改前
size_t MemTable::get_total_size() {
    std::shared_lock<std::shared_mutex> slock1(cur_mtx);
    std::shared_lock<std::shared_mutex> slock2(frozen_mtx);
    return get_frozen_size() + get_cur_size();  // 递归加锁！
}

// 修改后
size_t MemTable::get_total_size() {
    std::shared_lock<std::shared_mutex> slock1(cur_mtx);
    std::shared_lock<std::shared_mutex> slock2(frozen_mtx);
    return frozen_bytes + current_table->get_size();  // 直接访问成员
}
```

---

## 四、编译与部署

```bash
# 1. 停止旧进程
pkill -f lsm_server

# 2. 编译
cd ~/lsm/lsm/build
make -j$(nproc) lsm_server

# 3. 启动（必须在项目根目录，因为 config.toml 在此）
cd ~/lsm/lsm
nohup ./build/lsm_server > /tmp/lsm.log 2>&1 &

# 4. 验证
ps aux | grep lsm_server
ss -tlnp | grep lsm_server
nc -w 3 127.0.0.1 6380 <<< $'*1\r\n$4\r\nPING\r\n'
```

---

## 五、对外 RESP 接口

LSM 服务兼容 Redis RESP 协议，支持 34 个命令：

| 类别 | 命令 |
|------|------|
| 服务管理 | PING, FLUSHALL, SAVE |
| String | SET, GET, DEL, INCR, DECR, EXPIRE, TTL |
| Hash | HSET, HGET, HDEL, HKEYS |
| List | LPUSH, RPUSH, LPOP, RPOP, LLEN, LRANGE |
| ZSet | ZADD, ZREM, ZRANGE, ZCARD, ZSCORE, ZINCRBY, ZRANK |
| Set | SADD, SREM, SISMEMBER, SCARD, SMEMBERS |

连接方式：`redis-cli -h 43.157.39.171 -p 6380`

---

## 六、已知问题

1. **公网访问**：需在腾讯云安全组中放行 6380 端口
2. **INCR 可见性**：INCR 返回值可能非单调递增（LSM 引擎层 get→put 可见性问题，待进一步排查）
3. **崩溃堆栈**：当前未开启 core dump，建议执行：
   ```bash
   ulimit -c unlimited
   echo '/tmp/core.%p' | sudo tee /proc/sys/kernel/core_pattern
   ```

---

## 七、数据清理

LSM 数据存储在 `~/lsm/lsm/example_db/`，包含 SST 文件、WAL 和 vlog。

```bash
# 清空所有数据
rm -rf ~/lsm/lsm/example_db
# 重启服务后自动重建
```
