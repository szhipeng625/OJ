// Package api 提供 OJ 的 HTTP/JSON 接口。
package api

import (
	"encoding/json"
	"fmt"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync/atomic"
	"time"

	"OJ/internal/judge"
	"OJ/internal/raft"
)

type Server struct {
	store      *raft.Node
	problemsDir string
	submitSeq  atomic.Int64
}

func New(store *raft.Node, problemsDir string) *Server {
	return &Server{store: store, problemsDir: problemsDir}
}

func (s *Server) Routes() *http.ServeMux {
	mux := http.NewServeMux()
	mux.HandleFunc("/api/problems", s.listProblems)
	mux.HandleFunc("/api/problems/", s.getProblem)
	mux.HandleFunc("/api/submit", s.submit)
	mux.HandleFunc("/api/submissions", s.listSubmissions)
	return mux
}

// problem 题目元数据。
type problem struct {
	ID          int    `json:"id"`
	Title       string `json:"title"`
	Description string `json:"description"`
	SampleIn    string `json:"sampleIn"`
	SampleOut   string `json:"sampleOut"`
}

func writeJSON(w http.ResponseWriter, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	json.NewEncoder(w).Encode(v)
}

// GET /api/problems
func (s *Server) listProblems(w http.ResponseWriter, r *http.Request) {
	dirs, _ := filepath.Glob(filepath.Join(s.problemsDir, "*"))
	out := []problem{}
	for _, d := range dirs {
		idStr := filepath.Base(d)
		id, err := strconv.Atoi(idStr)
		if err != nil {
			continue
		}
		p := s.loadProblem(id)
		out = append(out, problem{ID: p.ID, Title: p.Title, Description: p.Description})
	}
	writeJSON(w, out)
}

// GET /api/problems/{id}
func (s *Server) getProblem(w http.ResponseWriter, r *http.Request) {
	idStr := strings.TrimPrefix(r.URL.Path, "/api/problems/")
	id, err := strconv.Atoi(idStr)
	if err != nil {
		http.Error(w, "bad id", http.StatusBadRequest)
		return
	}
	p := s.loadProblem(id)
	if p.Title == "" {
		http.NotFound(w, r)
		return
	}
	writeJSON(w, p)
}

func (s *Server) loadProblem(id int) problem {
	dir := filepath.Join(s.problemsDir, strconv.Itoa(id))
	p := problem{ID: id, Title: "题目 " + strconv.Itoa(id)}
	if b, err := readFile(filepath.Join(dir, "statement.txt")); err == nil {
		p.Description = b
	}
	if b, err := readFile(filepath.Join(dir, "sample.in")); err == nil {
		p.SampleIn = b
	}
	if b, err := readFile(filepath.Join(dir, "sample.out")); err == nil {
		p.SampleOut = b
	}
	return p
}

// submitRequest 提交请求。
type submitRequest struct {
	ProblemID int    `json:"problemId"`
	Code      string `json:"code"`
}

// POST /api/submit
func (s *Server) submit(w http.ResponseWriter, r *http.Request) {
	var req submitRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		http.Error(w, "bad body", http.StatusBadRequest)
		return
	}
	if strings.TrimSpace(req.Code) == "" {
		http.Error(w, "empty code", http.StatusBadRequest)
		return
	}

	dir := filepath.Join(s.problemsDir, strconv.Itoa(req.ProblemID))
	result, err := judge.Judge(dir, req.Code, 1*time.Second)
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}

	// 提交记录落 LSM（经 Raft 层）
	id := s.submitSeq.Add(1)
	rec := map[string]any{
		"id":        id,
		"problemId": req.ProblemID,
		"verdict":   result.Verdict,
		"detail":    result.Detail,
		"at":        time.Now().Format("2006-01-02 15:04:05"),
	}
	raw, _ := json.Marshal(rec)
	s.store.Put(r.Context(), fmt.Sprintf("submission:%d", id), string(raw))

	writeJSON(w, map[string]any{
		"id":      id,
		"verdict": result.Verdict,
		"detail":  result.Detail,
		"cases":   result.CaseRes,
	})
}

// GET /api/submissions 从 LSM 读最近提交记录。
func (s *Server) listSubmissions(w http.ResponseWriter, r *http.Request) {
	// 极简实现：扫 1..seq 读出来（LSM 暂未提供范围扫描接口）
	n := s.submitSeq.Load()
	out := []json.RawMessage{}
	for i := n; i >= 1 && i > n-20; i-- {
		v, ok, err := s.store.Get(r.Context(), fmt.Sprintf("submission:%d", i))
		if err != nil || !ok {
			continue
		}
		out = append(out, json.RawMessage(v))
	}
	writeJSON(w, out)
}

func readFile(p string) (string, error) {
	b, err := os.ReadFile(p)
	return string(b), err
}
