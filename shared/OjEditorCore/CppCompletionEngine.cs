using System.Text.RegularExpressions;

namespace OjEditorCore;

/// <summary>
/// 客户端与出题端共用的 C++ 补全引擎：内建关键字/常用语句 + 用户自定义标识符，
/// 按当前单词前缀过滤。用户定义的变量/函数会自动进入补全列表。
/// </summary>
public static class CppCompletionEngine
{
    private static readonly Regex IdentifierRegex =
        new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    // C++ 关键字（用于从文档标识符里排除，避免把关键字当成“用户变量”）
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "alignas", "alignof", "and", "and_eq", "asm", "auto", "bitand", "bitor",
        "bool", "break", "case", "catch", "char", "char8_t", "char16_t", "char32_t",
        "class", "compl", "concept", "const", "consteval", "constexpr", "constinit",
        "const_cast", "continue", "co_await", "co_return", "co_yield", "decltype",
        "default", "delete", "do", "double", "dynamic_cast", "else", "enum", "explicit",
        "export", "extern", "false", "float", "for", "friend", "goto", "if", "inline",
        "int", "long", "mutable", "namespace", "new", "noexcept", "not", "not_eq",
        "nullptr", "operator", "or", "or_eq", "private", "protected", "public",
        "register", "reinterpret_cast", "requires", "return", "short", "signed",
        "sizeof", "static", "static_assert", "static_cast", "struct", "switch",
        "template", "this", "thread_local", "throw", "true", "try", "typedef",
        "typeid", "typename", "union", "unsigned", "using", "virtual", "void",
        "volatile", "wchar_t", "while", "xor", "xor_eq", "override", "final",
    };

    private static readonly List<CompletionItem> BuiltinItems = new()
    {
        // 基础类型 / 修饰
        new("int", "整型"), new("long", "长整型"), new("long long", "长长整型"),
        new("short", "短整型"), new("double", "双精度浮点"), new("float", "单精度浮点"),
        new("char", "字符型"), new("bool", "布尔型"), new("void", "空类型"),
        new("auto", "自动类型推导"), new("const", "常量修饰"), new("static", "静态修饰"),
        new("unsigned", "无符号"), new("signed", "有符号"), new("virtual", "虚函数"),
        // 控制流
        new("return", "返回"), new("if", "if 语句"), new("else", "else 分支"),
        new("for", "for 循环"), new("while", "while 循环"), new("do", "do-while 循环"),
        new("switch", "switch 语句"), new("case", "case 分支"), new("default", "默认分支"),
        new("break", "跳出循环"), new("continue", "继续循环"), new("goto", "跳转"),
        // 结构
        new("namespace", "命名空间"), new("using", "using 声明"), new("class", "类定义"),
        new("struct", "结构体"), new("enum", "枚举"), new("template", "模板"),
        new("typedef", "类型别名"), new("typename", "类型名"),
        new("public", "公有"), new("private", "私有"), new("protected", "保护"),
        // 表达式 / 内存
        new("new", "动态分配"), new("delete", "释放内存"), new("sizeof", "大小运算符"),
        new("this", "当前对象指针"), new("nullptr", "空指针"), new("true", "真"), new("false", "假"),
        // 常用
        new("cin", "std::cin 标准输入"), new("cout", "std::cout 标准输出"),
        new("cerr", "std::cerr 标准错误"), new("endl", "std::endl 换行"), new("std", "std 命名空间"),
        new("scanf", "scanf 格式化输入"), new("printf", "printf 格式化输出"),
        new("memset", "memset 内存设置"), new("memcpy", "memcpy 内存拷贝"),
        new("malloc", "malloc 内存分配"), new("free", "free 内存释放"),
        new("sort", "std::sort 排序"), new("swap", "std::swap 交换"),
        new("min", "std::min 最小值"), new("max", "std::max 最大值"),
        // STL 容器
        new("vector", "std::vector 动态数组"), new("map", "std::map 有序映射"),
        new("set", "std::set 有序集合"), new("string", "std::string 字符串"),
        new("queue", "std::queue 队列"), new("stack", "std::stack 栈"),
        new("deque", "std::deque 双端队列"), new("list", "std::list 链表"),
        new("pair", "std::pair 键值对"), new("tuple", "std::tuple 元组"),
        new("array", "std::array 固定数组"), new("priority_queue", "std::priority_queue 优先队列"),
        new("unordered_map", "std::unordered_map 哈希映射"),
        new("unordered_set", "std::unordered_set 哈希集合"),
        // 头文件
        new("#include <iostream>", "输入输出流"), new("#include <cstdio>", "C 标准输入输出"),
        new("#include <cstring>", "C 字符串操作"), new("#include <cstdlib>", "C 标准库"),
        new("#include <cmath>", "数学函数"), new("#include <algorithm>", "STL 算法"),
        new("#include <vector>", "vector 容器"), new("#include <map>", "map 容器"),
        new("#include <set>", "set 容器"), new("#include <string>", "string 字符串"),
        new("#include <queue>", "queue 队列"), new("#include <stack>", "stack 栈"),
        new("#include <bits/stdc++.h>", "万能头"),
        new("using namespace std;", "使用 std 命名空间"),
    };

    private static readonly HashSet<string> BuiltinNames = new(
        BuiltinItems.Select(i => i.Text), StringComparer.Ordinal);

    /// <summary>提取文档里所有用户自定义标识符（排除关键字与内建词）。</summary>
    public static IEnumerable<string> ExtractIdentifiers(string documentText)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in IdentifierRegex.Matches(documentText ?? string.Empty))
        {
            string id = m.Value;
            if (id.Length > 64) continue;
            if (Keywords.Contains(id) || BuiltinNames.Contains(id)) continue;
            if (seen.Add(id)) yield return id;
        }
    }

    /// <summary>
    /// 按当前单词前缀返回补全项：用户自定义标识符优先，其次内建关键字/语句。
    /// </summary>
    public static List<CompletionItem> GetCompletions(string documentText, string prefix, int max = 200)
    {
        var result = new List<CompletionItem>();
        if (string.IsNullOrEmpty(prefix)) return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 用户自定义标识符优先（更贴近当前上下文）
        foreach (var id in ExtractIdentifiers(documentText))
        {
            if (!id.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (!seen.Add(id)) continue;
            result.Add(new CompletionItem(id, "自定义变量 / 函数", 10));
            if (result.Count >= max) return result;
        }

        // 内建关键字 / 常用语句
        foreach (var item in BuiltinItems)
        {
            if (!item.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(item.Text)) continue;
            result.Add(item);
            if (result.Count >= max) return result;
        }
        return result;
    }
}
