# LSM 服务器 Bug 修复记录

> 排查对象：远端 tiny-lsm RESP 服务器（`/home/ubuntu/lsm/lsm/`，监听 `0.0.0.0:6380`）
> 排查时间：2026-09-22
> 影响面：比赛提交详情（`submission:{id}`）、提交列表（`contest_subs:{cid}`）、以及一切使用 `INCR` 的命令

---

## 一、现象

1. 客户端提交比赛代码后，双击「查看代码」永远返回 `{"found":false}`。
2. `INCR meta:seq` 返回**非递增**的 id（例如先 `3` 再 `1`；更早一次 `1`、`1`）。
3. 直接对服务器发 `INCR` 会导致进程退出（6380 端口随后拒绝连接）。

---

## 二、Bug 1：`do_read` 只读一行就解析，大请求被截断（已修复）

### 根因

`server/src/server.cpp` 的 `do_read`：

```cpp
asio::async_read_until(socket_, buffer_, "\r\n", ...);   // 只读到第一个 "\r\n"（即 "*N\r\n" 头行）
...
for (int i = 0; i < numElements * 2; ++i) {
    if (buffer_.size() == 0) { do_write("-ERR partial request received"); return; }
    std::getline(is, line);      // 从 buffer_ 取 —— 剩余数据可能还没到达！
}
```

它**只调用一次** `async_read_until`，拿到头行后就假设剩下的 `numElements*2` 行都已经在 `buffer_` 里。

- 小命令（`PING`、小 `SET`）整个帧一个 TCP 段就到达 → 碰巧能解析。
- `submission:{id}` 的 JSON 有 1~2KB，跨多个 TCP 段 → `buffer_` 只有半截 → 解析报
  `bulk string length exceeds request size` → **SET 根本没写进去**，后续 `GET` 必然查不到。

### 修复

把 `do_read` 改成「递归读满所有行再交给 `handleRequest`」，新增两个辅助函数：

```cpp
// 从 buffer_ 取一行（去掉行尾 \r\n）；不足一行返回 false
bool read_line(std::string& out) {
    std::istream is(&buffer_);
    std::string line;
    if (!std::getline(is, line)) return false;
    if (!line.empty() && line.back() == '\r') line.pop_back();
    out = std::move(line);
    return true;
}

// 递归读取剩余 total_lines 行，拼出完整请求后交给 handleRequest
void read_rest(int total_lines, int done, std::string full_request) {
    auto self(shared_from_this());
    if (done >= total_lines) {
        do_write(handleRequest(full_request));
        return;
    }
    asio::async_read_until(socket_, buffer_, "\r\n",
        [this, self, total_lines, done, full_request](const asio::error_code& ec, std::size_t) mutable {
            if (ec) { ASYNC_REDIS_SERVER_LOG_INFO("Error on read: " << ec.message()); return; }
            std::string line;
            if (!read_line(line)) {
                do_write("-ERR Protocol error: partial request received\r\n");
                return;
            }
            full_request += line + "\r\n";
            read_rest(total_lines, done + 1, full_request);
        });
}

void do_read() {
    auto self(shared_from_this());
    asio::async_read_until(socket_, buffer_, "\r\n",
        [this, self](const asio::error_code& ec, std::size_t) {
            if (ec) {
                if (ec == asio::error::eof || ec == asio::error::connection_reset)
                    ASYNC_REDIS_SERVER_LOG_INFO("Connection closed from "
                        << socket_.remote_endpoint().address().to_string() << ":"
                        << socket_.remote_endpoint().port());
                else
                    ASYNC_REDIS_SERVER_LOG_INFO("Error on read: " << ec.message());
                return;
            }

            std::string first;
            if (!read_line(first)) { do_write("-ERR Protocol error\r\n"); return; }

            if (first == "PING") { do_write("+PONG\r\n"); return; }
            if (first.empty() || first[0] != '*') {
                do_write("-ERR Protocol error: expected '*'\r\n"); return;
            }
            int numElements = 0;
            try { numElements = std::stoi(first.substr(1)); }
            catch (const std::exception&) {
                do_write("-ERR Protocol error: invalid number of elements\r\n"); return;
            }

            read_rest(numElements * 2, 0, first + "\r\n");
        });
}
```

> 说明：旧代码 `request_line += "\r\n"` 会在每行行尾多出一个 `\r`；新代码用 `read_line`
> 去掉 `\r`，统一以 `\n` 收尾，行格式更干净。

---

## 三、Bug 2：`redis_incr` 未捕获 `std::stoll` 异常 → 进程崩溃

### 根因

`src/redis_wrapper/redis_wrapper.cpp`：

```cpp
std::string RedisWrapper::redis_incr(const std::string &key) {
    std::unique_lock<std::shared_mutex> lock(redis_mtx);
    auto original_vale = this->lsm->get(key);
    if (!original_vale.has_value()) {
        this->lsm->put(key, "1");
        return "1";
    }
    auto new_value = std::to_string(std::stoll(original_vale.value()) + 1);  // 可能抛异常
    this->lsm->put(key, new_value);
    return new_value;
}
```

`std::stoll` 在值为空串或非数字时抛 `std::invalid_argument`/`std::out_of_range`。
该异常沿 `incr_handler → handleRequest → asio 读回调` 一路向上，**asio 回调里不允许抛出异常**，
最终触发 `std::terminate` → 整个服务器退出。

### 修复

```cpp
std::string RedisWrapper::redis_incr(const std::string &key) {
    std::unique_lock<std::shared_mutex> lock(redis_mtx);
    auto original_vale = this->lsm->get(key);
    if (!original_vale.has_value()) {
        this->lsm->put(key, "1");
        return "1";
    }
    long long v = 0;
    try {
        v = std::stoll(original_vale.value());
    } catch (const std::exception&) {
        v = 0;   // 值不是合法整数时按 0 处理，避免异常击穿 asio 回调
    }
    auto new_value = std::to_string(v + 1);
    this->lsm->put(key, new_value);
    return new_value;
}
```

---

## 四、Bug 3：`MemTable::get_total_size()` 对同一把 `shared_mutex` 递归加读锁

### 根因

`src/memtable/memtable.cpp`：

```cpp
size_t MemTable::get_total_size() {
    std::shared_lock<std::shared_mutex> slock1(cur_mtx);
    std::shared_lock<std::shared_mutex> slock2(frozen_mtx);
    return get_frozen_size() + get_cur_size();   // 内部再次加锁
}

size_t MemTable::get_cur_size() {
    std::shared_lock<std::shared_mutex> slock(cur_mtx);   // 重复加 cur_mtx 读锁
    return current_table->get_size();
}
size_t MemTable::get_frozen_size() {
    std::shared_lock<std::shared_mutex> slock(frozen_mtx); // 重复加 frozen_mtx 读锁
    return frozen_bytes;
}
```

`std::shared_mutex` 不支持递归加锁。单线程下通常「碰巧能跑」，但一旦有写者等待（例如
多线程 flush / 并发写），会**死锁**；即使不崩也是未定义行为。

### 修复

在已持有两把锁的前提下直接读字段，不再重复加锁：

```cpp
size_t MemTable::get_total_size() {
    std::shared_lock<std::shared_mutex> slock1(cur_mtx);
    std::shared_lock<std::shared_mutex> slock2(frozen_mtx);
    return frozen_bytes + current_table->get_size();
}
```

---

## 五、INCR 返回非递增（LSM get→put 可见性）排查结论

已通读核心链路：`LSM::get/put` → `LSMEngine::get/put` → `MemTable::get/put`
（current/frozen 双表 + 冻结/刷盘）→ `SkipList::get/put`（同 key 按 tranc_id 降序）
→ `SST::get` / `BlockIterator` / `Block::get_idx_binary`（同 key 按 tranc_id 降序 + MVCC）。

关键事实：

- `nextTransactionId_` 有类内初始值 `= 1`，`getNextTransactionId()` 为 `fetch_add(1)`，
  同一进程内单调递增，因此 `put` 的 tranc_id 恒小于紧随其后的 `get` 的 tranc_id，MVCC 可见性理论上成立。
- `INCR` 命令帧很小（~44B），不存在 `do_read` 的大帧截断问题，且 `handleRequest` 用显式长度解析，
  旧代码行尾多出的 `\r` 不会污染参数。

结论：**非递增现象是 LSM 引擎层「put 后 get 不可见」的可见性 bug**，但该 bug 无法在
git 历史版源码中 100% 复现定位——很可能 Ubuntu 上实际运行的源码已与历史版有差异，
或需要结合 `/tmp/lsm.log` 中的崩溃栈进一步确认。

### 建议的下一步定位动作

1. 贴出 Ubuntu 上**当前实际**的 `server.cpp` / `redis_wrapper.cpp` / `memtable.cpp`
   与 git 历史版 diff，确认是否有本地改动。
2. 贴出 `/tmp/lsm.log` 中崩溃前后的日志（含 `spdlog` 的 put/get/flush trace 与异常栈）。
3. 用最小复现（重启后仅发 `INCR key` 三次 + `GET key`）观察：
   - 若 `GET` 也读不到刚 `SET` 的小值，则是基本 put/get 可见性 bug；
   - 若小值 SET/GET 正常、仅 INCR 异常，则问题集中在 get→put 的读改写链路。

> 客户端侧已彻底规避该问题：比赛提交的 sid 改用 MySQL `submissions.id`（AUTO_INCREMENT），
> 不再依赖服务器 `INCR`。因此本 bug 不影响 OJ 正常运行，仅影响「直接在 LSM 上用 INCR」的场景。
