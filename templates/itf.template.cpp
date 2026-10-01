// 接口编程题：只需实现 solve 函数，不要写 main 函数和输入输出语句。
// 提交后你的代码会自动编译为共享库（DLL/.so），由题目入口加载、解析输入并调用 solve，返回值即输出。
// 函数签名以题面为准（示例为两个整数求和）。
#ifdef _WIN32
extern "C" __declspec(dllexport)
#else
extern "C" __attribute__((visibility("default")))
#endif
int solve(int a, int b) {
    // TODO: 在此实现你的算法
    return a + b;
}
