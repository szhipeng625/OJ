// 题目入口 entry.cpp —— 函数式（接口）判题的加载/计时脚手架。
//
// 运行约定（重要，出题人需遵守，判题核心按此调用）：
//   entry.exe <user.dll> <输入文件> <输出文件> <时间文件>
//   - <user.dll>：用户实现接口后被编译出的 DLL（客户端本地 g++ 编译）。
//   - <输入文件>：本组测试数据的输入（.in）。
//   - <输出文件>：入口把用户接口函数的返回值写到这里，判题核心据此与标准答案比对。
//   - <时间文件>：入口把「仅调用接口函数的耗时」写到这里（毫秒，整数），判题核心读取后作为本题耗时。
//
// 职责顺序（务必保持）：
//   1. LoadLibrary 加载 user.dll；
//   2. 解析输入（按题面约定从 <输入文件> 读取参数）；
//   3. 只对 solve() 调用本身计时（输入解析 / 写输出不计入耗时）；
//   4. 把 solve 的返回值写入 <输出文件>；
//   5. 把耗时（毫秒）写入 <时间文件>。
//
// 修改点（按题面改这两处，其余保持不变）：
//   (a) 函数指针类型与函数名（与接口模板 itf.template.cpp 中声明的签名一致）；
//   (b) 「解析输入」与「输出结果」两部分。
#ifdef _WIN32
#include <windows.h>
typedef HMODULE lib_t;
static lib_t load_lib(const char* p) { return LoadLibraryA(p); }
static void* get_fn(lib_t h, const char* n) { return (void*)GetProcAddress(h, n); }
static void close_lib(lib_t h) { if (h) FreeLibrary(h); }
#else
#include <dlfcn.h>
typedef void* lib_t;
static lib_t load_lib(const char* p) { return dlopen(p, RTLD_LAZY); }
static void* get_fn(lib_t h, const char* n) { return dlsym(h, n); }
static void close_lib(lib_t h) { if (h) dlclose(h); }
#endif
#include <chrono>
#include <fstream>
#include <string>
using namespace std;

// (a) 按题面修改：与接口模板中 solve 的签名保持一致（示例：两个整数求和）。
typedef int (*solve_fn)(int, int);

int main(int argc, char** argv) {
    if (argc < 5) return 2;                 // 参数不足：lib/in/out/time
    const char* dllFile  = argv[1];
    const char* inFile   = argv[2];
    const char* outFile  = argv[3];
    const char* timeFile = argv[4];

    // 1) 加载用户共享库并取接口函数
    lib_t h = load_lib(dllFile);
    if (!h) return 3;                       // 库加载失败
    solve_fn solve = (solve_fn)get_fn(h, "solve");
    if (!solve) { close_lib(h); return 4; } // 未找到接口函数

    // 2) (b) 解析输入：按题面约定从 inFile 读参数
    ifstream fin(inFile);
    int a = 0, b = 0;
    fin >> a >> b;
    fin.close();

    // 3) 仅计时 solve 调用本身（输入解析 / 输出不计时）
    auto t0 = chrono::steady_clock::now();
    int ans = solve(a, b);
    auto t1 = chrono::steady_clock::now();

    // 4) (b) 输出结果：把返回值写入 outFile
    ofstream fout(outFile);
    fout << ans << "\n";
    fout.close();

    // 5) 写耗时（毫秒）
    ofstream ftime(timeFile);
    ftime << chrono::duration_cast<chrono::milliseconds>(t1 - t0).count() << "\n";
    ftime.close();

    close_lib(h);
    return 0;
}
