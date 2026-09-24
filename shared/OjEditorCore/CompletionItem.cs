using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;

namespace OjEditorCore;

/// <summary>客户端与出题端共用的补全项。</summary>
public class CompletionItem : ICompletionData
{
    public string Text { get; }
    public object Content => Text;
    public object Description { get; }
    public double Priority { get; }
    public ImageSource? Image => null;

    public CompletionItem(string text, string description, double priority = 0)
    {
        Text = text;
        Description = description;
        Priority = priority;
    }

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        textArea.Document.Replace(completionSegment, Text);
    }
}
