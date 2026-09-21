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
using System.IO;
using System.Xml;

namespace client.Presentation.Controls
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
            Editor.TextArea.PreviewKeyDown += TextArea_PreviewKeyDown;
        }

        // 高亮定义名称（XSHD 插件，见 Presentation/Highlighting/CppDarkPlus.xshd）
        private const string DarkDefinitionName = "C++ Dark+";
        private static readonly object HighlightInitLock = new();

        private void SetupCppHighlighting()
        {
            Editor.SyntaxHighlighting = LoadDarkPlusDefinition();
        }

        /// <summary>
        /// 加载内嵌的 C++ Dark+ 高亮插件（XSHD）。插件缺失时回退到 AvalonEdit 内置 C++ 定义。
        /// 想改配色/关键字：直接改 CppDarkPlus.xshd，无需动本文件。
        /// </summary>
        private static IHighlightingDefinition? LoadDarkPlusDefinition()
        {
            lock (HighlightInitLock)
            {
                var registered = HighlightingManager.Instance.GetDefinition(DarkDefinitionName);
                if (registered != null) return registered;

                try
                {
                    var asm = typeof(CodeEditor).Assembly;
                    var resName = asm.GetManifestResourceNames()
                        .FirstOrDefault(n => n.EndsWith("CppDarkPlus.xshd", StringComparison.OrdinalIgnoreCase));
                    if (resName != null)
                    {
                        using var stream = asm.GetManifestResourceStream(resName);
                        if (stream != null)
                        {
                            using var reader = new XmlTextReader(stream);
                            var def = HighlightingLoader.Load(reader, HighlightingManager.Instance);
                            HighlightingManager.Instance.RegisterHighlighting(DarkDefinitionName,
                                new[] { ".cpp", ".hpp", ".cc", ".cxx", ".h", ".c" }, def);
                            return def;
                        }
                    }
                }
                catch { }

                // 回退：AvalonEdit 内置 C++（浅色配色）
                return HighlightingManager.Instance.GetDefinition("C++");
            }
        }
        // 自动闭合括号 / 引号（基础语法补全）
        private static readonly Dictionary<char, string> AutoClosePairs = new()
        {
            ['{'] = "}", ['('] = ")", ['['] = "]", ['"'] = "\"", ['\''] = "'"
        };

        // C++ 基础语法补全项（轻量：只保留基础关键字与常用语句）
        private static readonly List<CompletionData> BasicKeywords = new()
        {
            // 基础类型 / 修饰
            new("int", "整型"), new("long", "长整型"), new("short", "短整型"),
            new("double", "双精度浮点"), new("float", "单精度浮点"),
            new("char", "字符型"), new("bool", "布尔型"), new("void", "空类型"),
            new("auto", "自动类型推导"), new("const", "常量修饰"), new("static", "静态修饰"),
            // 控制流
            new("return", "返回"), new("if", "if 语句"), new("else", "else 分支"),
            new("for", "for 循环"), new("while", "while 循环"), new("do", "do-while 循环"),
            new("switch", "switch 语句"), new("case", "case 分支"), new("default", "默认分支"),
            new("break", "跳出循环"), new("continue", "继续循环"), new("goto", "跳转"),
            // 结构
            new("namespace", "命名空间"), new("using", "using 声明"),
            new("class", "类定义"), new("struct", "结构体"), new("enum", "枚举"),
            new("template", "模板"), new("typedef", "类型别名"), new("typename", "类型名"),
            new("public", "公有"), new("private", "私有"), new("protected", "保护"),
            // 表达式 / 内存
            new("new", "动态分配"), new("delete", "释放内存"), new("sizeof", "大小运算符"),
            new("this", "当前对象指针"), new("nullptr", "空指针"),
            new("true", "真"), new("false", "假"),
            // 常用
            new("cin", "std::cin 标准输入"), new("cout", "std::cout 标准输出"),
            new("endl", "std::endl 换行"), new("std", "std 命名空间"),
        };

        private void TextArea_TextEntering(object sender, TextCompositionEventArgs e)
        {
            if (e.Text.Length == 0) return;

            // 基础语法补全：自动闭合括号 / 引号，光标留在中间
            if (e.Text.Length == 1 && AutoClosePairs.TryGetValue(e.Text[0], out var closer))
            {
                _completionWindow?.Close();
                var ta = Editor.TextArea;
                int caret = ta.Caret.Offset;
                ta.Document.Insert(caret, e.Text + closer);
                ta.Caret.Offset = caret + 1;
                e.Handled = true;
                return;
            }

            if (_completionWindow != null)
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

        private void TextArea_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var ta = Editor.TextArea;
            if (e.Key == Key.Back)
            {
                int off = ta.Caret.Offset;
                if (off > 0 && off < ta.Document.TextLength)
                {
                    char prev = ta.Document.GetCharAt(off - 1);
                    char next = ta.Document.GetCharAt(off);
                    if (IsMatchingPair(prev, next))
                    {
                        ta.Document.Remove(off - 1, 2);
                        e.Handled = true;
                    }
                }
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                HandleEnter(ta);
            }
        }

        private static bool IsMatchingPair(char a, char b)
        {
            return (a, b) switch
            {
                ('{', '}') or ('(', ')') or ('[', ']') or ('"', '"') or ('\'', '\'') => true,
                _ => false
            };
        }

        private void HandleEnter(TextArea ta)
        {
            var doc = ta.Document;
            int offset = ta.Caret.Offset;
            var line = doc.GetLineByOffset(offset);
            string lineText = doc.GetText(line.Offset, offset - line.Offset);

            int indentLen = 0;
            while (indentLen < lineText.Length && (lineText[indentLen] == ' ' || lineText[indentLen] == '\t'))
                indentLen++;
            string indent = new string(' ', indentLen);
            string nl = Environment.NewLine;

            // 光标夹在自动闭合的 {} 中间：拆成三行
            if (offset > line.Offset && offset < doc.TextLength &&
                doc.GetCharAt(offset - 1) == '{' && doc.GetCharAt(offset) == '}')
            {
                doc.Insert(offset, nl + indent + "    " + nl + indent);
                ta.Caret.Offset = offset + nl.Length + indent.Length + 4;
                return;
            }

            // 行尾是 { ：换行并缩进一级
            if (offset > line.Offset && doc.GetCharAt(offset - 1) == '{')
            {
                doc.Insert(offset, nl + indent + "    ");
                ta.Caret.Offset = offset + nl.Length + indent.Length + 4;
                return;
            }

            doc.Insert(offset, nl + indent);
            ta.Caret.Offset = offset + nl.Length + indent.Length;
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

            var matches = BasicKeywords
                     .Where(c => c.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                     .Take(200)
                     .ToList();
            if (matches.Count == 0) return;

            var window = new CompletionWindow(textArea)
            {
                Width = 320,
                Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30)),
                Foreground = Brushes.White,
                StartOffset = line.Offset + wordStart,   // ★ 关键
            };
            window.CompletionList.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            window.CompletionList.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
            window.CompletionList.Foreground = Brushes.White;

            foreach (var item in matches)
                window.CompletionList.CompletionData.Add(item);

            window.CompletionList.SelectItem(prefix);    // ★ 添加后再选中

            window.Closed += (s, e) => { if (_completionWindow == window) _completionWindow = null; };
            _completionWindow = window;
            window.Show();
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
