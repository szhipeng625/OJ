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
using System.Windows.Threading;
using OjEditorCore;

namespace author.Presentation.Controls
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

        // IsLight：true 时为白色背景的纯文本编辑（用于题目描述 Markdown，无 C++ 高亮与补全）
        public static readonly DependencyProperty IsLightProperty =
            DependencyProperty.Register("IsLight", typeof(bool), typeof(CodeEditor),
                new PropertyMetadata(false, OnIsLightChanged));

        public bool IsLight
        {
            get => (bool)GetValue(IsLightProperty);
            set => SetValue(IsLightProperty, value);
        }

        private static void OnIsLightChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((CodeEditor)d).ApplyTheme((bool)e.NewValue);
        }

        // ShowLineNumbers：是否显示行号（题目描述默认关闭）
        public static readonly DependencyProperty ShowLineNumbersProperty =
            DependencyProperty.Register("ShowLineNumbers", typeof(bool), typeof(CodeEditor),
                new PropertyMetadata(true, OnShowLineNumbersChanged));

        public bool ShowLineNumbers
        {
            get => (bool)GetValue(ShowLineNumbersProperty);
            set => SetValue(ShowLineNumbersProperty, value);
        }

        private static void OnShowLineNumbersChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((CodeEditor)d).Editor.ShowLineNumbers = (bool)e.NewValue;
        }

        // WordWrap：是否自动换行（题目描述默认开启）
        public static readonly DependencyProperty WordWrapProperty =
            DependencyProperty.Register("WordWrap", typeof(bool), typeof(CodeEditor),
                new PropertyMetadata(false, OnWordWrapChanged));

        public bool WordWrap
        {
            get => (bool)GetValue(WordWrapProperty);
            set => SetValue(WordWrapProperty, value);
        }

        private static void OnWordWrapChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((CodeEditor)d).Editor.WordWrap = (bool)e.NewValue;
        }

        public CodeEditor()
        {
            InitializeComponent();
            ApplyTheme(IsLight);
            Editor.TextChanged += (s, e) =>
            {
                if (Text != Editor.Text)
                    Text = Editor.Text;
            };
            Editor.TextArea.TextEntering += TextArea_TextEntering;
            Editor.TextArea.KeyDown += TextArea_KeyDown;
            Editor.TextArea.PreviewKeyDown += TextArea_PreviewKeyDown;
        }

        // ---------- 格式工具栏（Word 式 Markdown 插入） ----------

        /// <summary>用前后缀包裹选区（无选区则插入占位文本并把光标放到中间）。</summary>
        public void ApplyWrap(string before, string after, string placeholder = "")
        {
            var ta = Editor.TextArea;
            if (!ta.Selection.IsEmpty)
            {
                string sel = ta.Selection.GetText();
                ta.Selection.ReplaceSelectionWithText(before + sel + after);
            }
            else
            {
                int offset = ta.Caret.Offset;
                string insert = before + placeholder + after;
                ta.Document.Insert(offset, insert);
                ta.Caret.Offset = offset + before.Length + placeholder.Length;
            }
            Editor.Focus();
        }

        /// <summary>给选区每一行（无选区时当前行）加行首前缀。</summary>
        public void PrefixLines(string prefix)
        {
            var ta = Editor.TextArea;
            var doc = ta.Document;
            var seg = ta.Selection.SurroundingSegment;
            int start = doc.GetLineByOffset(seg.Offset).LineNumber;
            int end = doc.GetLineByOffset(seg.EndOffset).LineNumber;
            for (int i = end; i >= start; --i)
                doc.Insert(doc.GetLineByNumber(i).Offset, prefix);
            Editor.Focus();
        }

        /// <summary>在当前行前插入独立一行文本。</summary>
        public void InsertLine(string text)
        {
            var ta = Editor.TextArea;
            var doc = ta.Document;
            var line = doc.GetLineByOffset(ta.Caret.Offset);
            string nl = line.DelimiterLength > 0
                ? doc.GetText(line.Offset + line.Length, line.DelimiterLength)
                : Environment.NewLine;
            doc.Insert(line.Offset, text + nl);
            Editor.Focus();
        }

        // 高亮定义名称（XSHD 插件，见 Presentation/Highlighting/CppDarkPlus.xshd）
        private const string DarkDefinitionName = "C++ Dark+";
        private static readonly object HighlightInitLock = new();

        private void ApplyTheme(bool light)
        {
            if (light)
            {
                RootBorder.Background = Brushes.White;
                RootBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xE6, 0xEB));
                Editor.Background = Brushes.White;
                Editor.Foreground = Brushes.Black;
                Editor.LineNumbersForeground = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
                Editor.SyntaxHighlighting = null;
            }
            else
            {
                RootBorder.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
                RootBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
                Editor.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
                Editor.Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
                Editor.LineNumbersForeground = new SolidColorBrush(Color.FromRgb(0x85, 0x85, 0x85));
                Editor.SyntaxHighlighting = LoadDarkPlusDefinition();
            }
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
        // C++ 补全规则统一由 OjEditorCore.CppCompletionEngine 提供（关键字 + 用户自定义标识符）

        private void TextArea_TextEntering(object sender, TextCompositionEventArgs e)
        {
            if (IsLight) return;   // 纯文本（Markdown）模式：不触发 C++ 补全

            if (e.Text.Length > 0 && _completionWindow != null)
            {
                if (char.IsLetterOrDigit(e.Text[0]) || e.Text[0] == '_')
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(RefreshCompletion));
                    return;
                }
                _completionWindow.CompletionList.RequestInsertion(e);
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

        // 纯文本（题目描述 Markdown）模式：Tab 只缩进当前行，避免 AvalonEdit 默认的
        // “选区/整段缩进”导致下面几行跟着一起缩进。
        private void TextArea_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!IsLight || e.Key != Key.Tab) return;
            e.Handled = true;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                OutdentCurrentLine();
            else
                InsertTabIndent();
        }

        private void InsertTabIndent()
        {
            var ta = Editor.TextArea;
            int offset = ta.Caret.Offset;
            if (!ta.Selection.IsEmpty)
            {
                // 只缩进当前行：把选区折叠到起点，避免整段缩进
                offset = ta.Selection.SurroundingSegment.Offset;
                ta.Selection = Selection.Create(ta, offset, offset);
            }
            ta.Document.Insert(offset, "\t");
            ta.Caret.Offset = offset + 1;
        }

        private void OutdentCurrentLine()
        {
            var ta = Editor.TextArea;
            var doc = ta.Document;
            var line = doc.GetLineByOffset(ta.Caret.Offset);
            string text = doc.GetText(line.Offset, line.Length);
            int indent = text.StartsWith("\t") ? 1
                : text.StartsWith("    ") ? 4
                : text.StartsWith("  ") ? 2 : 0;
            if (indent > 0)
            {
                int caretCol = ta.Caret.Column;
                doc.Remove(line.Offset, indent);
                ta.Caret.Column = Math.Max(1, caretCol - indent);
            }
        }

        private (string prefix, int startOffset, List<CompletionItem> matches) ComputeCompletion()
        {
            var textArea = Editor.TextArea;
            int caret = textArea.Caret.Offset;
            var line = textArea.Document.GetLineByOffset(caret);
            string lineText = textArea.Document.GetText(line.Offset, caret - line.Offset);

            // 找到当前单词起始
            int wordStart = lineText.Length;
            while (wordStart > 0 && (char.IsLetterOrDigit(lineText[wordStart - 1]) || lineText[wordStart - 1] == '_' || lineText[wordStart - 1] == '#'))
                wordStart--;
            string prefix = lineText.Substring(wordStart);
            var matches = prefix.Length == 0
                ? new List<CompletionItem>()
                : CppCompletionEngine.GetCompletions(textArea.Document.Text, prefix, 200);
            return (prefix, line.Offset + wordStart, matches);
        }

        private void RefreshCompletion()
        {
            if (_completionWindow == null) return;
            var (prefix, _, matches) = ComputeCompletion();
            if (matches.Count == 0)
            {
                _completionWindow.Close();
                _completionWindow = null;
                return;
            }
            _completionWindow.CompletionList.CompletionData.Clear();
            foreach (var item in matches)
                _completionWindow.CompletionList.CompletionData.Add(item);
            _completionWindow.CompletionList.SelectItem(prefix);
        }

        private void ShowCompletion()
        {
            var (prefix, startOffset, matches) = ComputeCompletion();
            if (matches.Count == 0) return;

            var window = new CompletionWindow(Editor.TextArea)
            {
                Width = 320,
                Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30)),
                Foreground = Brushes.White,
                StartOffset = startOffset,
                CloseWhenCaretAtBeginning = false,
            };
            window.CompletionList.IsFiltering = false;   // 手动过滤：支持动态变量与空列表自动关闭
            window.CompletionList.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
            window.CompletionList.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
            window.CompletionList.Foreground = Brushes.White;

            foreach (var item in matches)
                window.CompletionList.CompletionData.Add(item);

            window.CompletionList.SelectItem(prefix);
            window.Closed += (s, e) => { if (_completionWindow == window) _completionWindow = null; };
            _completionWindow = window;
            window.Show();
        }
    }
}
