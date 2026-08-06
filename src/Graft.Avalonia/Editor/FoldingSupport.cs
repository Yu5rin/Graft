using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using Graft.Core;

namespace Graft.Editor;

/// <summary>
/// コードの折りたたみ（4.4節）。インデントベースを既定とし、C系（<c>{}</c>を持つ言語）は
/// 括弧ベースで折りたたみ範囲を求める。<see cref="AvaloniaEdit.Folding.FoldingManager"/>を
/// <see cref="TextEditor"/>へ直接インストールして動作するため、エディタへの組み込み
/// （<see cref="Attach"/>の呼び出し・<see cref="Dispose"/>のタイミング管理）は統合担当が行う。
/// 18章の性能要件により、再計算は編集のたびではなくデバウンスして行う。
/// WPF版（AvalonEdit）からの移植。FoldingManager/NewFoldingのAPIはAvaloniaEditでも
/// 同名同形のため、名前空間の差し替えのみで移植できる。
/// </summary>
public sealed class FoldingSupport : IDisposable
{
    private const int RecalculateDebounceMs = 300;

    // C#/JavaScript・TypeScript等、{}でブロックを表す言語は括弧ベースで折りたたむ。
    // それ以外（Python・HTML・Markdown等）はインデントベースにする（4.4節）。
    private static readonly HashSet<string> BraceBasedLanguageNames = new(StringComparer.Ordinal)
    {
        "C#", "JavaScript/TypeScript", "CSS", "JSON",
    };

    private readonly TextEditor _editor;
    private readonly DispatcherTimer _debounceTimer;
    private FoldingManager? _manager;
    private bool _enabled = true;
    private bool _useBraceStrategy;
    private TextDocument? _document;
    private bool _disposed;

    public FoldingSupport(TextEditor editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RecalculateDebounceMs) };
        _debounceTimer.Tick += OnDebounceTick;
    }

    /// <summary>15章 <c>editor.folding</c> 設定の反映。無効化するとインストール済みの
    /// <see cref="FoldingManager"/>を解除する。</summary>
    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        if (!enabled) Uninstall();
        else if (_document is not null) Attach(_document, _useBraceStrategy);
    }

    /// <summary>対象ドキュメントと言語（拡張子）を切り替える（タブ切替のたび呼ぶ）。</summary>
    public void Attach(TextDocument document, string extension)
    {
        var rule = SyntaxLexer.RuleForExtension(extension);
        Attach(document, rule is not null && BraceBasedLanguageNames.Contains(rule.Name));
    }

    private void Attach(TextDocument document, bool useBraceStrategy)
    {
        ArgumentNullException.ThrowIfNull(document);
        DetachDocument();

        _document = document;
        _useBraceStrategy = useBraceStrategy;
        if (!_enabled) return;

        _manager = FoldingManager.Install(_editor.TextArea);
        document.Changed += OnDocumentChanged;
        RecalculateNow();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounceTimer.Stop();
        DetachDocument();
    }

    private void DetachDocument()
    {
        if (_document is not null) _document.Changed -= OnDocumentChanged;
        _debounceTimer.Stop();
        Uninstall();
        _document = null;
    }

    private void Uninstall()
    {
        if (_manager is null) return;
        FoldingManager.Uninstall(_manager);
        _manager = null;
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounceTimer.Stop();
        RecalculateNow();
    }

    private void RecalculateNow()
    {
        if (_manager is null || _document is null) return;
        var foldings = _useBraceStrategy
            ? BraceFoldingStrategy.ComputeFoldings(_document)
            : IndentFoldingStrategy.ComputeFoldings(_document);
        _manager.UpdateFoldings(foldings, -1);
    }
}

/// <summary>C系言語向けの括弧ベース折りたたみ。<c>{</c> <c>}</c> の対応のみを深さで数える
/// 簡易実装で、文字列・コメント内の括弧は区別しない（性能を優先した簡略化）。</summary>
internal static class BraceFoldingStrategy
{
    public static IEnumerable<NewFolding> ComputeFoldings(TextDocument document)
    {
        var foldings = new List<NewFolding>();
        var starts = new Stack<int>();

        foreach (var line in document.Lines)
        {
            var text = document.GetText(line.Offset, line.Length);
            for (var col = 0; col < text.Length; col++)
            {
                if (text[col] == '{')
                {
                    starts.Push(line.Offset + col);
                }
                else if (text[col] == '}' && starts.Count > 0)
                {
                    var startOffset = starts.Pop();
                    var endOffset = line.Offset + col + 1;
                    if (document.GetLineByOffset(startOffset).LineNumber != line.LineNumber)
                    {
                        foldings.Add(new NewFolding(startOffset, endOffset));
                    }
                }
            }
        }

        foldings.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
        return foldings;
    }
}

/// <summary>
/// インデントベースの折りたたみ（既定）。行頭の空白幅が自分より深い行が連続する間を
/// 1つの折りたたみ範囲とする（Pythonのブロック構造等を想定）。空行は開始・終了の判定に使わない。
/// </summary>
internal static class IndentFoldingStrategy
{
    public static IEnumerable<NewFolding> ComputeFoldings(TextDocument document)
    {
        var foldings = new List<NewFolding>();
        var stack = new Stack<(int Indent, DocumentLine Line)>();
        DocumentLine? lastNonBlank = null;

        foreach (var line in document.Lines)
        {
            var text = document.GetText(line.Offset, line.Length);
            if (string.IsNullOrWhiteSpace(text)) continue;

            var indent = LeadingWhitespaceLength(text);
            while (stack.Count > 0 && stack.Peek().Indent >= indent)
            {
                var entry = stack.Pop();
                if (lastNonBlank is { } last) ClosePendingFold(foldings, entry, last);
            }
            stack.Push((indent, line));
            lastNonBlank = line;
        }

        while (stack.Count > 0)
        {
            var entry = stack.Pop();
            if (lastNonBlank is { } last) ClosePendingFold(foldings, entry, last);
        }

        foldings.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
        return foldings;
    }

    private static void ClosePendingFold(
        List<NewFolding> foldings, (int Indent, DocumentLine StartLine) entry, DocumentLine lastChildLine)
    {
        if (lastChildLine.LineNumber <= entry.StartLine.LineNumber) return; // 子を持たない行は折りたためない
        var endOffset = lastChildLine.Offset + lastChildLine.Length;
        foldings.Add(new NewFolding(entry.StartLine.Offset + entry.StartLine.Length, endOffset));
    }

    private static int LeadingWhitespaceLength(string text)
    {
        var i = 0;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
        return i;
    }
}
