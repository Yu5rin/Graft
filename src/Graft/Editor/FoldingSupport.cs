using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using Graft.Core;
using Graft.ViewModels;

namespace Graft.Editor;

/// <summary>
/// コードの折りたたみ（4.4節）。インデントベースを既定とし、C系（<c>{}</c>を持つ言語）は
/// 括弧ベースで折りたたみ範囲を求める。<see cref="AvaloniaEdit.Folding.FoldingManager"/>を
/// <see cref="TextEditor"/>へ直接インストールして動作するため、エディタへの組み込み
/// （<see cref="Attach"/>の呼び出し・<see cref="Dispose"/>のタイミング管理）は統合担当が行う。
/// 18章の性能要件により、再計算は編集のたびではなくデバウンスして行う。
/// v2.0のWPF版（AvalonEdit）からの移植。FoldingManager/NewFoldingのAPIはAvaloniaEditでも
/// 同名同形のため、名前空間の差し替えのみで移植できる。
///
/// 課題3（再設計）: 以前は極端に長い行を含むファイルではこの機能自体をファイル全体で
/// 無効化していた。<see cref="RecalculateNow"/>（<see cref="BraceFoldingStrategy"/>/
/// <see cref="IndentFoldingStrategy"/>）は各行を1回ずつ読んで文字を走査するだけの
/// 線形処理（1文字ごとにレキサを呼び直すような二乗コストの経路が無い）のため、実測では
/// 1行10万文字のファイルで1ms未満、3万行＋1行10万文字が混在するファイルでも最大19ms程度
/// だった（デバウンス300msの予算に対して十分小さい）。このコストなら無効化する理由が
/// 無いため、極端に長い行の有無に関わらず常に利用者の設定（<c>editor.folding</c>）へ
/// そのまま従う（EditorPane.axaml.cs参照。無効化していたのはEditorPane側の判定であり、
/// 本クラス自体に長い行を特別扱いするコードは元々存在しない）。
///
/// 不具合1（実機で確認された未処理例外の修正）: Windows実機で
/// <c>System.ArgumentException: Invalid document at AvaloniaEdit.Folding.
/// FoldingElementGenerator.StartGeneration</c> が未処理のままアプリごと落ちる不具合が
/// 報告された。
///
/// 【真因】 <see cref="AvaloniaEdit.Folding.FoldingManager"/>は<c>Install</c>した瞬間の
/// <c>TextArea.Document</c>に紐づいて生成される。呼び出し側（<see cref="Views.EditorPane"/>）は
/// タブ切替のたび「<c>Editor.Document</c>を新しい文書へ差し替える → <see cref="Attach"/>を
/// 呼んで古い<see cref="FoldingManager"/>をuninstallし新しい文書で作り直す」という2つの
/// 文を続けて実行するが、この2文の間には一瞬「<c>Editor.Document</c>は新しい文書だが、
/// インストール済みの<see cref="FoldingManager"/>（＝<see cref="FoldingElementGenerator"/>）は
/// まだ古い文書に紐づいたまま」という食い違った状態が存在する。この間にAvaloniaEditの
/// レイアウト/描画パス（<c>DispatcherPriority.Render</c>で非同期に走るジョブ。デバウンスタイマー
/// の発火・折り返しの再計算・ウィンドウリサイズ等、UIスレッドの別ジョブとして割り込みうる）が
/// 差し込まれると、<see cref="FoldingElementGenerator.StartGeneration"/>が
/// 「レンダリング対象の文書とFoldingManagerが保持する文書が一致しない」と判定し
/// <see cref="ArgumentException"/>（"Invalid document"）を投げる。この例外は
/// <c>Avalonia.Threading.DispatcherOperation.InvokeCore</c>から素通りで
/// <c>AppDomain.UnhandledException</c>まで抜けるため、このアプリ側のどのtry/catchにも
/// 引っかからずプロセスごと終了する（tests/Graft.UiTests/EditorTests.csの再現テストで
/// 修正前のコードが実際にこの例外で失敗することを確認済み）。
///
/// 【対処】 <see cref="TextEditor.DocumentChanged"/>はAvaloniaのプロパティ変更通知の仕組みにより
/// <c>Editor.Document = 新しい文書</c>という代入そのものの中で同期的に発火する（代入が
/// 呼び出し元へ返ってきた時点で購読側の処理は完了している）。本クラスのコンストラクタで
/// この事件を直接購読し、その場で古い<see cref="FoldingManager"/>を同期的にuninstallする
/// ことで、「<c>Editor.Document</c>は新しいが<see cref="FoldingManager"/>は古いまま」という
/// 食い違った状態が1行たりとも存在しなくなる。呼び出し側（<see cref="Views.EditorPane"/>）の
/// 文の並び順（Document代入→<see cref="Attach"/>呼び出し）に依存しないため、統合担当側の
/// コードを変更する必要が無い（タブ切替・タブを閉じる・空タブ化のいずれの経路も
/// 内部的には<c>Editor.Document</c>の代入を経由するため、この1箇所の対処で全経路をカバーする）。
/// なお再読込（<see cref="Editor.DocumentSession.ReloadAsync"/>）は同一の<see cref="TextDocument"/>
/// インスタンスの<c>Text</c>を書き換えるだけで<c>Editor.Document</c>自体は差し替わらないため、
/// この食い違いは元々発生しない。
///
/// デバウンスタイマー（<see cref="OnDebounceTick"/>）の発火は<see cref="DispatcherTimer.Stop"/>で
/// 大半のケースを防げるが、念のため<see cref="RecalculateNow"/>側でも発火時点の
/// <c>Editor.Document</c>が取り付け対象と一致するかを二重に確認する。加えて、
/// <see cref="FoldingManager"/>への操作（Install/Uninstall/UpdateFoldings）は万一
/// AvaloniaEdit側の内部状態と食い違っても例外を外へ漏らさないよう<c>try/catch</c>で囲み、
/// <see cref="SafeHandler.OnUnexpected"/>へ記録したうえで折りたたみ1回分の更新を諦めるに
/// 留める（アプリ本体は継続させる。附録A.4・設計目標5）。
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

        // 不具合1: Editor.Documentが差し替わった瞬間に古いFoldingManagerを同期的に
        // uninstallする（クラスコメントの「対処」参照）。
        _editor.DocumentChanged += OnEditorDocumentChanged;
    }

    /// <summary>
    /// 不具合1: <see cref="TextEditor.Document"/>が変わった瞬間に同期的に発火する。
    /// 呼び出し側が<see cref="Attach"/>を呼び直すより前に、古い文書に紐づいた
    /// <see cref="FoldingManager"/>をここで確実にuninstallしておく。
    /// </summary>
    private void OnEditorDocumentChanged(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(_document, _editor.Document)) DetachDocument();
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

        // 不具合1: FoldingManager.Installはこの瞬間のEditor.Documentに紐づいて作られる。
        // 呼び出し側は本来Editor.Document==documentの状態でAttachを呼ぶ契約だが、万一
        // 食い違っていた場合にInvalid documentの温床となる不整合なFoldingManagerを
        // 作らないよう、ここで確認してから取り付ける。
        if (!ReferenceEquals(_editor.Document, document))
        {
            SafeHandler.OnUnexpected?.Invoke(
                "折りたたみの取り付け",
                new InvalidOperationException(
                    "FoldingSupport.Attachに渡された文書がEditor.Documentと一致しません。"));
            return;
        }

        try
        {
            _manager = FoldingManager.Install(_editor.TextArea);
        }
        catch (Exception ex)
        {
            // 万一AvaloniaEdit側の内部状態と食い違っても、折りたたみを諦めるだけに留め
            // アプリは継続させる（附録A.4・設計目標5）。
            _manager = null;
            SafeHandler.OnUnexpected?.Invoke("折りたたみの取り付け", ex);
            return;
        }

        document.Changed += OnDocumentChanged;
        RecalculateNow();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _editor.DocumentChanged -= OnEditorDocumentChanged;
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
        var manager = _manager;
        _manager = null;
        try
        {
            FoldingManager.Uninstall(manager);
        }
        catch (Exception ex)
        {
            SafeHandler.OnUnexpected?.Invoke("折りたたみの解除", ex);
        }
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

        // 不具合1: デバウンスタイマーの発火は最大300ms遅延するため、発火時点で
        // Editor.Documentが取り付け対象からすでに差し替わっていないかを必ず確認する
        // （Stop()で大半は防げるが、クラスコメントのとおり念のための二重の防御）。
        if (!ReferenceEquals(_document, _editor.Document)) return;

        try
        {
            var foldings = _useBraceStrategy
                ? BraceFoldingStrategy.ComputeFoldings(_document)
                : IndentFoldingStrategy.ComputeFoldings(_document);
            _manager.UpdateFoldings(foldings, -1);
        }
        catch (Exception ex)
        {
            SafeHandler.OnUnexpected?.Invoke("折りたたみの再計算", ex);
        }
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
