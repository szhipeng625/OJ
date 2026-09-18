// Package raft 对外提供一个一致性存储接口。
//
// 当前是单机实现：直接把读写落到本地 LSM。
// 这一层是"分布式"的预留点——要接 HMETCD 那套 Raft 时，
// 只需把 Put/Delete 改成"经 Raft 日志复制后再 Apply 到 LSM"，
// Get 改成 ReadIndex 读主节点，上层 API 完全不用动。
package raft

import (
	"context"
	"log"

	"OJ/internal/lsm"
)

// Store 一致性 KV 接口（单机/分布式都实现它）。
type Store interface {
	Put(ctx context.Context, key, value string) error
	Get(ctx context.Context, key string) (string, bool, error)
	Delete(ctx context.Context, key string) error
}

// Node 单机节点：包装 LSM。
type Node struct {
	engine *lsm.Engine
	id     string
}

// NewSingleNode 创建单机节点（数据落在 dataDir）。
func NewSingleNode(id, dataDir string) (*Node, error) {
	eng, err := lsm.Open(dataDir, 256) // MemTable 满 256 条就 flush 成 SSTable
	if err != nil {
		return nil, err
	}
	log.Printf("[raft] 单机节点 %s 启动，数据目录 %s", id, dataDir)
	return &Node{engine: eng, id: id}, nil
}

// Put 写。分布式版本会先复制 Raft 日志。
func (n *Node) Put(_ context.Context, key, value string) error {
	return n.engine.Put(key, value)
}

// Get 读。分布式版本走 ReadIndex。
func (n *Node) Get(_ context.Context, key string) (string, bool, error) {
	return n.engine.Get(key)
}

// Delete 删。
func (n *Node) Delete(_ context.Context, key string) error {
	return n.engine.Delete(key)
}

// Close 关闭节点。
func (n *Node) Close() error { return n.engine.Close() }
