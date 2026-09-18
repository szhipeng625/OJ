// Package judge 负责把用户提交的代码编译、在测试点上限时运行并比对输出。
package judge

import (
	"bytes"
	"context"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"strings"
	"time"
)

// Result 一次判题的汇总结果。
type Result struct {
	Verdict  string         `json:"verdict"`  // AC / WA / TLE / RE / CE
	Detail   string         `json:"detail"`   // 人类可读说明
	CaseRes  []CaseResult   `json:"cases"`    // 每个测试点
	CompileMs int64        `json:"compileMs"`
}

// CaseResult 单个测试点结果。
type CaseResult struct {
	Name string `json:"name"`
	TimeMs int  `json:"timeMs"`
	Passed bool `json:"passed"`
	Info   string `json:"info"`
}

// Judge 对 problemDir 下的测试点评测 code（C++ 源码）。
// problemDir 里有 1.in/1.out、2.in/2.out ...
func Judge(problemDir, code string, timeLimit time.Duration) (*Result, error) {
	tmpDir, err := os.MkdirTemp("", "oj-judge-*")
	if err != nil {
		return nil, err
	}
	defer os.RemoveAll(tmpDir)

	cppPath := filepath.Join(tmpDir, "solution.cpp")
	exePath := filepath.Join(tmpDir, "solution.exe")
	if err := os.WriteFile(cppPath, []byte(code), 0644); err != nil {
		return nil, err
	}

	// 1) 编译
	t0 := time.Now()
	compileCmd := exec.Command("g++", "-O2", "-o", exePath, cppPath)
	var cerr bytes.Buffer
	compileCmd.Stderr = &cerr
	if err := compileCmd.Run(); err != nil {
		return &Result{
			Verdict: "CE",
			Detail:  "编译失败：" + truncate(cerr.String(), 500),
		}, nil
	}
	compileMs := time.Since(t0).Milliseconds()

	// 2) 收集测试点
	ins, _ := filepath.Glob(filepath.Join(problemDir, "*.in"))
	res := &Result{CompileMs: compileMs}
	verdict := "AC"

	for _, inPath := range ins {
		base := strings.TrimSuffix(filepath.Base(inPath), ".in")
		outPath := filepath.Join(problemDir, base+".out")
		cr := CaseResult{Name: base}

		info, ms, pass, err := runCase(exePath, inPath, outPath, timeLimit)
		cr.TimeMs = ms
		cr.Info = info
		cr.Passed = pass
		res.CaseRes = append(res.CaseRes, cr)

		switch {
		case err == errTLE:
			verdict = "TLE"
		case !pass && err == nil:
			if verdict == "AC" {
				verdict = "WA"
			}
		case err != nil:
			verdict = "RE"
		}
	}

	if len(ins) == 0 {
		return nil, fmt.Errorf("题目目录 %s 下没有测试点 *.in", problemDir)
	}
	res.Verdict = verdict
	res.Detail = summarize(verdict, len(res.CaseRes))
	return res, nil
}

var errTLE = fmt.Errorf("time limit exceeded")

// runCase 在单个测试点上运行，返回 (信息, 耗时ms, 是否通过, 错误)。
func runCase(exePath, inPath, ansPath string, limit time.Duration) (string, int, bool, error) {
	ctx, cancel := context.WithTimeout(context.Background(), limit)
	defer cancel()

	input, _ := os.ReadFile(inPath)
	ans, _ := os.ReadFile(ansPath)

	cmd := exec.CommandContext(ctx, exePath)
	var stdout, stderr bytes.Buffer
	cmd.Stdin = bytes.NewReader(input)
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr

	t0 := time.Now()
	err := cmd.Run()
	ms := int(time.Since(t0).Milliseconds())

	if ctx.Err() == context.DeadlineExceeded {
		return "超时", ms, false, errTLE
	}
	if err != nil {
		return "运行错误: " + truncate(stderr.String(), 200), ms, false, fmt.Errorf("run: %w", err)
	}

	if normalize(stdout.String()) == normalize(string(ans)) {
		return "通过", ms, true, nil
	}
	return "输出与标准答案不一致", ms, false, nil
}

// normalize 去掉 \r、行尾空白、末尾空行，避免格式小差异误判。
var trailingSpace = regexp.MustCompile("[ \t]+$")

func normalize(s string) string {
	s = strings.ReplaceAll(s, "\r", "")
	lines := strings.Split(s, "\n")
	for i, l := range lines {
		lines[i] = trailingSpace.ReplaceAllString(l, "")
	}
	// 去掉末尾空行
	for len(lines) > 0 && strings.TrimSpace(lines[len(lines)-1]) == "" {
		lines = lines[:len(lines)-1]
	}
	return strings.Join(lines, "\n")
}

func summarize(v string, n int) string {
	switch v {
	case "AC":
		return fmt.Sprintf("全部 %d 个测试点通过", n)
	case "WA":
		return "存在测试点输出错误"
	case "TLE":
		return "存在测试点超时"
	case "RE":
		return "存在测试点运行时错误"
	}
	return v
}

func truncate(s string, n int) string {
	if len(s) > n {
		return s[:n] + "..."
	}
	return s
}
