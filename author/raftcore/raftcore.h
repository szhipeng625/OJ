#pragma once
// raftcore.h — 轻量 Raft 共识引擎（VS 动态库，Win32 TCP）
// 参考 AdvisKV 的 Storage Raft 设计：选举（PreVote 简化版）、日志复制、多数提交、状态机应用。
#ifdef RAFTCORE_EXPORTS
#define RK_API __declspec(dllexport)
#else
#define RK_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

// 状态机应用回调：payload 为已提交的日志条目内容
typedef void (*RaftApplyFn)(const char* payload, int len, void* user);

typedef struct RaftConfig {
    int node_id;              // 本节点 ID（0 起）
    const char* listen_addr;  // 监听地址 "127.0.0.1:9101"
    const char* const* peers; // 其他节点地址数组（不含自己）
    int peer_count;           // 对端数量（0 = 单节点模式，立即自选 Leader）
    const char* persist_dir;  // Raft 状态持久化目录
} RaftConfig;

// 初始化 Raft 节点（内部启动监听/定时器线程）。返回 0 成功。
RK_API int rk_init(const RaftConfig* cfg, RaftApplyFn apply, void* user);

// 提交一个条目（仅 Leader 有效）。返回条目日志 index（>=0），-1 失败/非 Leader。
RK_API long long rk_propose(const char* payload, int len);

// 等待指定 index 的条目被提交（Leader 侧）。返回 0 已提交，-1 超时，-2 非 Leader。
RK_API int rk_wait_commit(long long index, int timeout_ms);

RK_API int  rk_is_leader(void);
RK_API int  rk_leader_id(void);
RK_API int  rk_current_term(void);
RK_API long long rk_commit_index(void);
RK_API long long rk_log_size(void);

// 停止节点（关闭线程与 socket）。返回 0。
RK_API int rk_shutdown(void);

#ifdef __cplusplus
}
#endif
