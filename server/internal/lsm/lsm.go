// Package lsm 实现一个最小可用的 LSM-Tree KV 引擎。
//
// 结构（参考 tiny-lsm / LevelDB 的设计，做了极简裁剪）：
//   - MemTable：内存 map，写入先进这里；
//   - WAL：每次写先追加日志，宕机后可重放；
//   - SSTable：MemTable 超过阈值就排序落盘成二进制文件；
//   - Get：先查 MemTable，再从新到旧扫 SSTable。
//
// 够用即可：存题目元数据、提交记录；不做 compaction（后续可加）。
package lsm

import (
	"encoding/binary"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
)

type Engine struct {
	mu        sync.Mutex
	dir       string
	mem       map[string]string // 内存表；值含 tombstone 标记 "\x00TOMB"
	wal       *os.File
	threshold int // MemTable 超过多少条就 flush
	seq       int // SSTable 文件序号
}

var tombstone = "\x00__TOMBSTONE__"

// Open 打开（或创建）一个 LSM 引擎，dir 为数据目录。
func Open(dir string, threshold int) (*Engine, error) {
	if err := os.MkdirAll(dir, 0755); err != nil {
		return nil, err
	}
	e := &Engine{
		dir:       dir,
		mem:       make(map[string]string),
		threshold: threshold,
		seq:       0,
	}
	// 找到已有最大 SSTable 序号
	entries, _ := os.ReadDir(dir)
	for _, ent := range entries {
		var n int
		if _, err := fmt.Sscanf(ent.Name(), "sst_%d.log", &n); err == nil && n >= e.seq {
			e.seq = n + 1
		}
	}
	// 重放 WAL
	walPath := filepath.Join(dir, "wal.log")
	if f, err := os.Open(walPath); err == nil {
		e.replay(f)
		f.Close()
	}
	// 重新打开 WAL 用于追加
	f, err := os.OpenFile(walPath, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0644)
	if err != nil {
		return nil, err
	}
	e.wal = f
	return e, nil
}

// replay 从 WAL 重建 MemTable。
func (e *Engine) replay(r io.Reader) {
	data, _ := io.ReadAll(r)
	for _, line := range strings.Split(string(data), "\n") {
		if line == "" {
			continue
		}
		parts := strings.SplitN(line, "\t", 3)
		switch parts[0] {
		case "P":
			if len(parts) == 3 {
				e.mem[parts[1]] = parts[2]
			}
		case "D":
			if len(parts) == 2 {
				e.mem[parts[1]] = tombstone
			}
		}
	}
}

// Put 写入 key=value。
func (e *Engine) Put(key, value string) error {
	e.mu.Lock()
	defer e.mu.Unlock()
	if _, err := fmt.Fprintf(e.wal, "P\t%s\t%s\n", key, value); err != nil {
		return err
	}
	e.mem[key] = value
	if len(e.mem) >= e.threshold {
		return e.flushLocked()
	}
	return nil
}

// Delete 删除 key（写 tombstone）。
func (e *Engine) Delete(key string) error {
	e.mu.Lock()
	defer e.mu.Unlock()
	if _, err := fmt.Fprintf(e.wal, "D\t%s\n", key); err != nil {
		return err
	}
	e.mem[key] = tombstone
	if len(e.mem) >= e.threshold {
		return e.flushLocked()
	}
	return nil
}

// Get 读取 key。ok=false 表示不存在。
func (e *Engine) Get(key string) (string, bool, error) {
	e.mu.Lock()
	defer e.mu.Unlock()
	if v, ok := e.mem[key]; ok {
		if v == tombstone {
			return "", false, nil
		}
		return v, true, nil
	}
	// 从新到旧扫 SSTable
	entries, _ := os.ReadDir(e.dir)
	ssts := []string{}
	for _, ent := range entries {
		if strings.HasPrefix(ent.Name(), "sst_") {
			ssts = append(ssts, ent.Name())
		}
	}
	sort.Sort(sort.Reverse(sort.StringSlice(ssts)))
	for _, name := range ssts {
		v, found, err := e.lookupSST(filepath.Join(e.dir, name), key)
		if err != nil {
			return "", false, err
		}
		if found {
			if v == tombstone {
				return "", false, nil
			}
			return v, true, nil
		}
	}
	return "", false, nil
}

// lookupSST 在单个 SSTable 文件里二分查找。
func (e *Engine) lookupSST(path, key string) (string, bool, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return "", false, err
	}
	if len(data) < 4 {
		return "", false, nil
	}
	count := int(binary.LittleEndian.Uint32(data[:4]))
	type pair struct {
		k, v string
	}
	all := make([]pair, 0, count)
	off := 4
	for i := 0; i < count; i++ {
		kl := int(binary.LittleEndian.Uint32(data[off : off+4]))
		off += 4
		k := string(data[off : off+kl])
		off += kl
		vl := int(binary.LittleEndian.Uint32(data[off : off+4]))
		off += 4
		v := string(data[off : off+vl])
		off += vl
		all = append(all, pair{k, v})
	}
	idx := sort.Search(len(all), func(i int) bool { return all[i].k >= key })
	if idx < len(all) && all[idx].k == key {
		return all[idx].v, true, nil
	}
	return "", false, nil
}

// flushLocked 把 MemTable 排序后写成 SSTable，并清空内存表与 WAL。
// 调用方必须已持有锁。
func (e *Engine) flushLocked() error {
	keys := make([]string, 0, len(e.mem))
	for k := range e.mem {
		keys = append(keys, k)
	}
	sort.Strings(keys)

	name := filepath.Join(e.dir, fmt.Sprintf("sst_%05d.log", e.seq))
	e.seq++
	f, err := os.Create(name)
	if err != nil {
		return err
	}
	defer f.Close()

	var buf [4]byte
	binary.LittleEndian.PutUint32(buf[:], uint32(len(keys)))
	f.Write(buf[:])
	for _, k := range keys {
		v := e.mem[k]
		kb, vb := []byte(k), []byte(v)
		binary.LittleEndian.PutUint32(buf[:], uint32(len(kb)))
		f.Write(buf[:])
		f.Write(kb)
		binary.LittleEndian.PutUint32(buf[:], uint32(len(vb)))
		f.Write(buf[:])
		f.Write(vb)
	}

	// 清空内存表与 WAL
	e.mem = make(map[string]string)
	e.wal.Close()
	os.Remove(filepath.Join(e.dir, "wal.log"))
	e.wal, err = os.OpenFile(filepath.Join(e.dir, "wal.log"),
		os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0644)
	return err
}

// Close 关闭引擎。
func (e *Engine) Close() error {
	e.mu.Lock()
	defer e.mu.Unlock()
	e.flushLocked()
	return e.wal.Close()
}
