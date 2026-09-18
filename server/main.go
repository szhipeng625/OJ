// OJ 在线判题系统 —— 服务端入口
//
// 架构：
//   WPF 客户端 ──HTTP/JSON──▶ api(net/http)
//                                │
//                                ├─▶ judge   编译+限时运行+比对
//                                └─▶ raft(单机) ──▶ lsm(MemTable+WAL+SSTable)
//
// 用法：
//   go run . -addr :8080 -problems ./problems -data ./data
package main

import (
	"flag"
	"log"
	"net/http"

	"OJ/internal/api"
	"OJ/internal/raft"
)

func main() {
	addr := flag.String("addr", ":8080", "监听地址")
	problemsDir := flag.String("problems", "./problems", "题目目录")
	dataDir := flag.String("data", "./data", "LSM 数据目录")
	flag.Parse()

	node, err := raft.NewSingleNode("node1", *dataDir)
	if err != nil {
		log.Fatalf("启动存储失败: %v", err)
	}
	defer node.Close()

	srv := api.New(node, *problemsDir)
	log.Printf("OJ 服务端已启动: http://localhost%s", *addr)
	log.Printf("题目目录: %s", *problemsDir)
	if err := http.ListenAndServe(*addr, srv.Routes()); err != nil {
		log.Fatal(err)
	}
}
