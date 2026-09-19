using System;
using System.Windows.Controls;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using System.Xml;

namespace client.Controls
{
    public partial class CodeEditor : UserControl
    {
        private CompletionWindow? _completionWindow;

        // Text 依赖属性
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register("Text", typeof(string), typeof(CodeEditor),
                new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnTextChanged));

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var editor = (CodeEditor)d;
            var newText = (string)e.NewValue ?? "";
            if (editor.Editor.Text != newText)
                editor.Editor.Text = newText;
        }

        // IsReadOnly 依赖属性
        public static readonly DependencyProperty IsReadOnlyProperty =
            DependencyProperty.Register("IsReadOnly", typeof(bool), typeof(CodeEditor),
                new PropertyMetadata(false, OnReadOnlyChanged));

        public bool IsReadOnly
        {
            get => (bool)GetValue(IsReadOnlyProperty);
            set => SetValue(IsReadOnlyProperty, value);
        }

        private static void OnReadOnlyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var editor = (CodeEditor)d;
            editor.Editor.IsReadOnly = (bool)e.NewValue;
            editor.Editor.ShowLineNumbers = !(bool)e.NewValue;
        }

        public CodeEditor()
        {
            InitializeComponent();
            SetupCppHighlighting();
            Editor.TextChanged += (s, e) =>
            {
                if (Text != Editor.Text)
                    Text = Editor.Text;
            };
            Editor.TextArea.TextEntering += TextArea_TextEntering;
            Editor.TextArea.KeyDown += TextArea_KeyDown;
        }

        private void SetupCppHighlighting()
        {
            try
            {
                // 尝试加载内置 C++ 高亮
                var builtIn = HighlightingManager.Instance.GetDefinition("C++");
                if (builtIn != null)
                {
                    Editor.SyntaxHighlighting = builtIn;
                    return;
                }
            }
            catch { }

            // 自定义 C++ 高亮
            Editor.SyntaxHighlighting = CreateCppHighlighting();
        }

        private static IHighlightingDefinition CreateCppHighlighting()
        {
            var xshd = @"<?xml version=""1.0""?>
<SyntaxDefinition name=""C++"" xmlns=""http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008"">
  <Color name=""Comment"" foreground=""#6A9955"" fontStyle=""Italic""/>
  <Color name=""String"" foreground=""#CE9178""/>
  <Color name=""Char"" foreground=""#CE9178""/>
  <Color name=""Number"" foreground=""#B5CEA8""/>
  <Color name=""Keyword"" foreground=""#569CD6"" fontWeight=""bold""/>
  <Color name=""Type"" foreground=""#4EC9B0""/>
  <Color name=""Preprocessor"" foreground=""#C586C0""/>
  <Color name=""Function"" foreground=""#DCDCAA""/>

  <RuleSet>
    <Span color=""Comment"" multiline=""true"">
      <Begin><!--</Begin>
      <End>--></End>
    </Span>
    <Span color=""Comment"">
      <Begin>//</Begin>
    </Span>
    <Span color=""String"">
      <Begin>""</Begin>
      <End>""</End>
      <RuleSet>
        <Span begin=""\\"" end=""[^0-9a-fA-FxXuU]"" color=""String""/>
      </RuleSet>
    </Span>
    <Span color=""Char"">
      <Begin>'</Begin>
      <End>'</End>
    </Span>
    <Span color=""Preprocessor"">
      <Begin>^\s*#</Begin>
    </Span>

    <Keywords color=""Keyword"">
      <Word>alignas</Word><Word>alignof</Word><Word>and</Word><Word>and_eq</Word>
      <Word>asm</Word><Word>auto</Word><Word>bitand</Word><Word>bitor</Word>
      <Word>bool</Word><Word>break</Word><Word>case</Word><Word>catch</Word>
      <Word>char</Word><Word>char8_t</Word><Word>char16_t</Word><Word>char32_t</Word>
      <Word>class</Word><Word>compl</Word><Word>concept</Word><Word>const</Word>
      <Word>consteval</Word><Word>constexpr</Word><Word>constinit</Word><Word>const_cast</Word>
      <Word>continue</Word><Word>co_await</Word><Word>co_return</Word><Word>co_yield</Word>
      <Word>decltype</Word><Word>default</Word><Word>delete</Word><Word>do</Word>
      <Word>double</Word><Word>dynamic_cast</Word><Word>else</Word><Word>enum</Word>
      <Word>explicit</Word><Word>export</Word><Word>extern</Word><Word>false</Word>
      <Word>float</Word><Word>for</Word><Word>friend</Word><Word>goto</Word>
      <Word>if</Word><Word>inline</Word><Word>int</Word><Word>long</Word>
      <Word>mutable</Word><Word>namespace</Word><Word>new</Word><Word>noexcept</Word>
      <Word>not</Word><Word>not_eq</Word><Word>nullptr</Word><Word>operator</Word>
      <Word>or</Word><Word>or_eq</Word><Word>private</Word><Word>protected</Word>
      <Word>public</Word><Word>register</Word><Word>reinterpret_cast</Word><Word>requires</Word>
      <Word>return</Word><Word>short</Word><Word>signed</Word><Word>sizeof</Word>
      <Word>static</Word><Word>static_assert</Word><Word>static_cast</Word><Word>struct</Word>
      <Word>switch</Word><Word>template</Word><Word>this</Word><Word>thread_local</Word>
      <Word>throw</Word><Word>true</Word><Word>try</Word><Word>typedef</Word>
      <Word>typeid</Word><Word>typename</Word><Word>union</Word><Word>unsigned</Word>
      <Word>using</Word><Word>virtual</Word><Word>void</Word><Word>volatile</Word>
      <Word>wchar_t</Word><Word>while</Word><Word>xor</Word><Word>xor_eq</Word>
    </Keywords>

    <Keywords color=""Type"">
      <Word>std</Word><Word>string</Word><Word>vector</Word><Word>map</Word>
      <Word>set</Word><Word>unordered_map</Word><Word>unordered_set</Word><Word>pair</Word>
      <Word>queue</Word><Word>stack</Word><Word>deque</Word><Word>list</Word>
      <Word>array</Word><Word>tuple</Word><Word>optional</Word><Word>variant</Word>
      <Word>function</Word><Word>shared_ptr</Word><Word>unique_ptr</Word><Word>weak_ptr</Word>
      <Word>iterator</Word><Word>size_t</Word><Word>ptrdiff_t</Word><Word>int8_t</Word>
      <Word>int16_t</Word><Word>int32_t</Word><Word>int64_t</Word><Word>uint8_t</Word>
      <Word>uint16_t</Word><Word>uint32_t</Word><Word>uint64_t</Word><Word>fstream</Word>
      <Word>ifstream</Word><Word>ofstream</Word><Word>stringstream</Word><Word>istream</Word>
      <Word>ostream</Word><Word>iostream</Word><Word>bitset</Word><Word>complex</Word>
    </Keywords>

    <Rule color=""Number"">\b\d+(\.\d+)?([eE][+-]?\d+)?[fFlLuU]?\b</Rule>
    <Rule color=""Number"">\b0[xX][0-9a-fA-F]+[lLuU]?\b</Rule>
    <Rule color=""Number"">\b0[0-7]+[lLuU]?\b</Rule>
  </RuleSet>
</SyntaxDefinition>";

            using var reader = new XmlTextReader(new System.IO.StringReader(xshd));
            return HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }

        // C++ 补全项
        private static readonly List<CompletionData> CompletionItems = new()
        {
            // 关键字
            new("int", "int 类型"), new("long", "long 类型"), new("double", "double 类型"),
            new("float", "float 类型"), new("char", "char 类型"), new("bool", "bool 类型"),
            new("void", "void 类型"), new("auto", "自动类型推导"), new("const", "常量修饰"),
            new("static", "静态修饰"), new("virtual", "虚函数"), new("inline", "内联函数"),
            new("namespace", "命名空间"), new("using", "using 声明"), new("class", "类定义"),
            new("struct", "结构体"), new("enum", "枚举"), new("template", "模板"),
            new("return", "返回"), new("if", "if 语句"), new("else", "else 分支"),
            new("for", "for 循环"), new("while", "while 循环"), new("do", "do-while 循环"),
            new("switch", "switch 语句"), new("case", "case 分支"), new("break", "跳出循环"),
            new("continue", "继续循环"), new("try", "异常捕获"), new("catch", "捕获异常"),
            new("throw", "抛出异常"), new("new", "动态分配"), new("delete", "释放内存"),
            new("sizeof", "大小运算符"), new("typedef", "类型别名"), new("typename", "类型名"),
            new("this", "当前对象指针"), new("nullptr", "空指针"), new("true", "真"),
            new("false", "假"), new("public", "公有"), new("private", "私有"),
            new("protected", "保护"), new("friend", "友元"), new("explicit", "显式构造"),
            new("operator", "运算符重载"), new("override", "重写"), new("final", "最终"),
            new("default", "默认"), new("noexcept", "不抛异常"), new("constexpr", "常量表达式"),
            new("decltype", "类型推导"), new("static_cast", "静态转换"), new("dynamic_cast", "动态转换"),
            new("reinterpret_cast", "重解释转换"), new("const_cast", "常量转换"),
            // STL 容器
            new("vector", "std::vector 动态数组"), new("map", "std::map 有序映射"),
            new("set", "std::set 有序集合"), new("unordered_map", "std::unordered_map 哈希映射"),
            new("unordered_set", "std::unordered_set 哈希集合"), new("string", "std::string 字符串"),
            new("queue", "std::queue 队列"), new("stack", "std::stack 栈"),
            new("deque", "std::deque 双端队列"), new("list", "std::list 链表"),
            new("pair", "std::pair 键值对"), new("tuple", "std::tuple 元组"),
            new("array", "std::array 固定数组"), new("bitset", "std::bitset 位集"),
            new("priority_queue", "std::priority_queue 优先队列"),
            // 常用函数
            new("cin", "std::cin 标准输入"), new("cout", "std::cout 标准输出"),
            new("cerr", "std::cerr 标准错误"), new("endl", "std::endl 换行刷新"),
            new("scanf", "scanf 格式化输入"), new("printf", "printf 格式化输出"),
            new("memset", "memset 内存设置"), new("memcpy", "memcpy 内存拷贝"),
            new("strlen", "strlen 字符串长度"), new("strcmp", "strcmp 字符串比较"),
            new("strcpy", "strcpy 字符串拷贝"), new("atoi", "atoi 字符串转整数"),
            new("atof", "atof 字符串转浮点"), new("malloc", "malloc 内存分配"),
            new("free", "free 内存释放"), new("sort", "std::sort 排序"),
            new("swap", "std::swap 交换"), new("min", "std::min 最小值"),
            new("max", "std::max 最大值"), new("abs", "std::abs 绝对值"),
            new("sqrt", "sqrt 平方根"), new("pow", "pow 幂运算"),
            new("sin", "sin 正弦"), new("cos", "cos 余弦"),
            new("floor", "floor 向下取整"), new("ceil", "ceil 向上取整"),
            new("rand", "rand 随机数"), new("srand", "srand 设置随机种子"),
            new("time", "time 获取时间"), new("clock", "clock 时钟"),
            new("lower_bound", "std::lower_bound 下界"), new("upper_bound", "std::upper_bound 上界"),
            new("binary_search", "std::binary_search 二分查找"), new("reverse", "std::reverse 反转"),
            new("unique", "std::unique 去重"), new("count", "std::count 计数"),
            new("find", "std::find 查找"), new("fill", "std::fill 填充"),
            new("accumulate", "std::accumulate 累加"), new("push_back", "push_back 尾部添加"),
            new("pop_back", "pop_back 尾部删除"), new("emplace_back", "emplace_back 尾部构造"),
            // 头文件
            new("#include <iostream>", "输入输出流"), new("#include <cstdio>", "C 标准输入输出"),
            new("#include <cstring>", "C 字符串操作"), new("#include <cstdlib>", "C 标准库"),
            new("#include <cmath>", "数学函数"), new("#include <algorithm>", "STL 算法"),
            new("#include <vector>", "vector 容器"), new("#include <map>", "map 容器"),
            new("#include <set>", "set 容器"), new("#include <string>", "string 字符串"),
            new("#include <queue>", "queue 队列"), new("#include <stack>", "stack 栈"),
            new("#include <bitset>", "bitset 位集"), new("#include <utility>", "pair 等工具"),
            new("#include <tuple>", "tuple 元组"), new("#include <unordered_map>", "unordered_map"),
            new("#include <unordered_set>", "unordered_set"), new("#include <fstream>", "文件流"),
            new("#include <sstream>", "字符串流"), new("#include <iomanip>", "格式化输出"),
            new("#include <ctime>", "时间函数"), new("#include <climits>", "整数极限"),
            new("#include <cfloat>", "浮点极限"), new("#include <numeric>", "数值算法"),
            new("#include <functional>", "函数对象"), new("#include <memory>", "智能指针"),
            new("using namespace std;", "使用 std 命名空间"),
        };

        private void TextArea_TextEntering(object sender, TextCompositionEventArgs e)
        {
            if (e.Text.Length > 0 && _completionWindow != null)
            {
                if (!char.IsLetterOrDigit(e.Text[0]) && e.Text[0] != '_')
                {
                    _completionWindow.CompletionList.RequestInsertion(e);
                }
                return;
            }

            if (!char.IsLetter(e.Text[0]) && e.Text[0] != '_' && e.Text[0] != '#')
                return;

            ShowCompletion();
        }

        private void TextArea_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                ShowCompletion();
            }
        }

        private void ShowCompletion()
        {
            var textArea = Editor.TextArea;
            var caretOffset = textArea.Caret.Offset;
            var line = textArea.Document.GetLineByOffset(caretOffset);
            var lineText = textArea.Document.GetText(line.Offset, caretOffset - line.Offset);

            // 找到当前单词起始
            int wordStart = lineText.Length;
            while (wordStart > 0 && (char.IsLetterOrDigit(lineText[wordStart - 1]) || lineText[wordStart - 1] == '_' || lineText[wordStart - 1] == '#'))
                wordStart--;
            var prefix = lineText.Substring(wordStart);
            if (prefix.Length == 0) return;

            var matches = CompletionItems
                .Where(c => c.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0) return;

            _completionWindow = new CompletionWindow(textArea)
            {
                Width = 320,
                Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30)),
                Foreground = Brushes.White,
            };
            _completionWindow.CompletionList.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            _completionWindow.CompletionList.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
            _completionWindow.CompletionList.Foreground = Brushes.White;
            _completionWindow.CompletionList.SelectedItem = matches[0];

            foreach (var item in matches)
                _completionWindow.CompletionList.CompletionData.Add(item);

            _completionWindow.Show();
            _completionWindow.Closed += (s, e) => _completionWindow = null;
        }
    }

    public class CompletionData : ICompletionData
    {
        public string Text { get; }
        public object Content => Text;
        public object Description { get; }
        public double Priority => 0;
        public System.Windows.Media.ImageSource? Image => null;

        public CompletionData(string text, string description)
        {
            Text = text;
            Description = description;
        }

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            textArea.Document.Replace(completionSegment, Text);
        }
    }
}
