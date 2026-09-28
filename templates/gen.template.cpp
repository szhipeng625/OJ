// 数据生成器 {name}.cpp
// 运行约定（重要）：
//   1. 程序只往 stdout（cout）输出测试数据，不要写文件、不要 freopen。
//   2. argv[1] = 随机种子（每次运行一个整数）；不读它也可以。
//   3. 后台自动捕获 stdout：
//      - 只有一个测试点：全部输出写入 1.in。
//      - 多个测试点：测试点之间用【单独一行 ###】分隔，后台切成 1.in / 2.in / 3.in ...
//      例如：
//        cout << "3 5\n";          // 第 1 个测试点
//        cout << "###\n";          // 分隔行（必须单独一行，整行只有 ###）
//        cout << "1 2\n";          // 第 2 个测试点
//   4. 输出格式必须与标程读入格式完全一致。
#include <bits/stdc++.h>
using namespace std;

int main(int argc, char** argv) {
    int seed = (argc > 1) ? atoi(argv[1]) : 1;
    mt19937 rng((unsigned)seed);
    uniform_int_distribution<int> dist(1, 100);

    // 生成第 1 个测试点
    int n = dist(rng);
    cout << n << "\n";
    for (int i = 0; i < n; ++i) cout << (i ? " " : "") << dist(rng);
    cout << "\n";

    // 生成第 2 个测试点（用 ### 分隔）
    cout << "###\n";
    int m = dist(rng);
    cout << m << "\n";
    for (int i = 0; i < m; ++i) cout << (i ? " " : "") << dist(rng);
    cout << "\n";

    return 0;
}
