using System.Text.RegularExpressions;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;

namespace Graft.Editor;

/// <summary>
/// 開いているファイル内の識別子を候補とする単語ベース補完（4.4節・Ctrl+Space）。
/// LSP等の意味解析は行わない（21章対象外）。<see cref="TextEditor"/>を直接受け取り、
/// <see cref="ICSharpCode.AvalonEdit.CodeCompletion.CompletionWindow"/>で候補を表示する。
/// エディタへの組み込み（Ctrl+Spaceの購読）は統合担当が行う。
/// </summary>
public sealed class CompletionProvider
{
    private static readonly Regex WordPattern = new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);
    private const int MinWordLength = 2;
    private const int MaxCandidates = 200;

    private readonly TextEditor _editor;
    private CompletionWindow? _window;

    public CompletionProvider(TextEditor editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
    }

    /// <summary>Ctrl+Spaceで呼び出す。候補が1件も無ければ何もしない。</summary>
    public void RequestCompletion()
    {
        var caretOffset = _editor.CaretOffset;
        var wordStart = FindWordStart(caretOffset);
        var prefix = _editor.Document.GetText(wordStart, caretOffset - wordStart);

        var candidates = CollectCandidates(prefix, wordStart, caretOffset);
        if (candidates.Count == 0) return;

        ShowWindow(wordStart, candidates);
    }

    private int FindWordStart(int caretOffset)
    {
        var doc = _editor.Document;
        var offset = caretOffset;
        while (offset > 0 && IsWordChar(doc.GetCharAt(offset - 1))) offset--;
        return offset;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private List<string> CollectCandidates(string prefix, int wordStart, int caretOffset)
    {
        var text = _editor.Document.Text;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (Match m in WordPattern.Matches(text))
        {
            if (m.Length < MinWordLength) continue;
            if (m.Index == wordStart && m.Index + m.Length == caretOffset) continue; // 入力中の単語自身は除外
            if (prefix.Length > 0 && !m.Value.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (!seen.Add(m.Value)) continue;

            result.Add(m.Value);
            if (result.Count >= MaxCandidates) break;
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private void ShowWindow(int wordStart, List<string> candidates)
    {
        _window = new CompletionWindow(_editor.TextArea) { StartOffset = wordStart };
        foreach (var word in candidates)
        {
            _window.CompletionList.CompletionData.Add(new WordCompletionData(word));
        }
        _window.Closed += (_, _) => _window = null;
        _window.Show();
    }
}

/// <summary>
/// 単語ベース補完の1候補。挿入するテキストのみを持つ単純な実装（説明・アイコンは持たない。
/// LSP等の意味解析は行わないため21章の対象外機能に踏み込まない）。
/// </summary>
internal sealed class WordCompletionData : ICompletionData
{
    public WordCompletionData(string text) => Text = text;

    public ImageSource? Image => null;
    public string Text { get; }
    public object Content => Text;
    public object Description => string.Empty;
    public double Priority => 0;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        => textArea.Document.Replace(completionSegment, Text);
}
