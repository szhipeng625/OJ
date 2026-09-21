// test_meta.cpp — 验证：authorcore 保存 meta/发布 + ojcore 读限制判题
// test_meta.cpp — 独立测试程序（不参与 ojcore.dll 编译）
// 需同时链接 authorcore.dll 和 ojcore.dll
#include "authorcore.h"
#include "ojcore.h"

#include <cstdio>
#include <cstdlib>
#include <cstring>

static void show(const char* tag, const char* s) {
    printf("[%s] %s\n", tag, s ? s : "(null)");
}

int main() {
    // ---- 出题端 ----
    int arc = ac_init("D:\\OJ\\author\\problems");
    printf("[ac_init] rc=%d\n", arc);
    show("save_meta", ac_save_meta(3, 2000, 64, "[\"基础\",\"求和\"]"));
    show("publish", ac_publish(3, "D:\\OJ\\server\\problems"));

    // ---- 判题端 ----
    int rc = oj_init("D:\\OJ\\server\\problems", "D:\\OJ\\ojdata_t");
    printf("[oj_init] rc=%d\n", rc);
    show("oj_problems", oj_get_problems());

    const char* code =
        "#include <iostream>\n"
        "using namespace std;\n"
        "int main(){ int a,b; cin>>a>>b; cout<<a+b<<endl; return 0; }";
    show("oj_submit", oj_submit(3, code));
    return 0;
}
