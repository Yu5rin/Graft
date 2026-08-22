using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using FluentAssertions;
using Graft.Editor;
using Graft.Infra;
using Graft.UiTests.TestSupport;

namespace Graft.UiTests;

/// <summary>
/// 折りたたみ・インデントガイドの「文書の寿命」回帰テスト（利用者報告の未処理例外2種のうち、
/// 実機ログで「適用の21ミリ秒後」という決定的なタイミング証拠が残っていた方＝
/// <see cref="Editor.IndentGuideRenderer.CollectActiveFoldSegments"/>由来の
/// <see cref="InvalidOperationException"/>の根治）。
///
/// 【課題#60・課題#69との違い】 どちらも「<c>Editor.Document</c>そのものを新しい
/// <see cref="TextDocument"/>インスタンスへ差し替える」経路（タブ切替）だけを検証・対策していた
/// （<see cref="EditorTests"/>の「文書差し替え後の折りたたみ更新で例外が出ない」参照）。
/// しかし実機で報告された例外は「適用（パッチの取り込み）に伴う再読込」の直後に発生していた。
/// <see cref="DocumentSession.ReloadAsync"/>（外部変更検知の再読込も同じ経路。
/// <see cref="Graft.ViewModels.EditorPaneViewModel.NotifyExternalChangeAsync"/>・
/// <see cref="Graft.Editor.EditorTabManager.ReloadIfOpenAsync"/>参照）は<c>Editor.Document</c>
/// 自体は差し替えず、同一の<see cref="TextDocument"/>インスタンスの<c>Text</c>プロパティを
/// 丸ごと書き換えるだけ（<c>Document.Text = newText</c>は内部で<c>Replace(0, oldLength,
/// newText)</c>、つまり「文書全体を1回のReplaceで削除＋挿入」する）。この経路は
/// <see cref="FoldingSupport"/>の「不具合1」対策（<c>Editor.DocumentChanged</c>購読、
/// <c>Editor.Document</c>の参照そのものが変わったときだけ発火する）の対象外であり、かつ
/// 以前は未検証だった。
///
/// 【コードと実機相当の実験で裏取りした真因】
/// AvaloniaEditの<c>TextView</c>は、文書の変更が実際に適用される「前」に文書の
/// <c>Changing</c>イベントを受けて<c>Redraw(offset, length)</c>（<c>TextView.OnChanging</c>）
/// を呼ぶ。<c>Redraw(int, int)</c>は変更範囲と重なる<c>VisualLine</c>を内部リスト
/// （<c>_allVisualLines</c>）から取り除いて<c>InvalidateMeasure()</c>を呼ぶだけで、外部公開用の
/// <c>_visibleVisualLines</c>（<c>TextView.VisualLines</c>プロパティ・<c>VisualLinesValid</c>が
/// 参照する方）はその場では更新しない（更新は次の<c>MeasureOverride</c>まで先送りされる）。
/// 文書全体を1回で置き換える<c>Document.Text = newText</c>では、この「削除+挿入」がoffset 0
/// から文書全体を覆うため、直前に存在した可視行がすべて対象になる。
///
/// 実際に本テストクラスの調査段階で、<c>document.Changed</c>ハンドラの中（＝
/// <c>Document.Text = newText</c>という1文の実行中、まだ次のレイアウトパスへ制御が渡る前）で
/// <c>TextView.VisualLinesValid</c>を読むと<c>true</c>のままであり、かつ
/// <c>TextView.VisualLines</c>が返す<see cref="AvaloniaEdit.Rendering.VisualLine"/>のうち、
/// 置き換え前の文書で存在した（置き換え後は文書から切り離された＝<c>IsDeleted</c>な）
/// <see cref="DocumentLine"/>を握ったままのものが混在することを、AvaloniaEdit 11.1.0の
/// 該当ソース（<c>TextView.cs</c>・<c>DocumentLine.cs</c>）を読んだ上で実機相当の手順
/// （headlessでの直接呼び出し）で確認した。この状態で
/// <see cref="Editor.IndentGuideRenderer.CollectActiveFoldSegments"/>が
/// <c>visualLine.FirstDocumentLine.Offset</c>（<see cref="DocumentLine.Offset"/>）へ触れると
/// <see cref="InvalidOperationException"/>（"Operation is not valid due to the current state of
/// the object."）を投げる。実機ログの「適用の21ミリ秒後」というタイミングは、この「置き換え
/// 直後・次のレイアウトパス完了前」という一瞬の食い違いの窓に、何らかの経路（描画・他の
/// Changedハンドラ等）で再描画が割り込んだことを示す証拠と一致する。
///
/// つまりタブ切替（<c>FoldingManager</c>が古い文書を握ったまま）とは別の、「同一文書インスタンス
/// の内容を丸ごと置き換えたときにAvaloniaEdit自身のTextViewが一瞬古いVisualLineを指したままに
/// なる」という、文書側ではなくAvaloniaEdit内部のレンダリング状態の寿命の食い違いが真因だった
/// （<see cref="FoldingManager"/>自体は文書インスタンスが変わらない限り常に正しい文書を指して
/// おり、こちらの経路には無関係。<c>FoldingManager.Document</c>はコンストラクタで一度だけ
/// 設定される不変フィールドであることをソースで確認済み）。
///
/// 【対処】 <see cref="Editor.IndentGuideRenderer.Draw"/>に、<c>textView.VisualLines</c>の
/// 各行が握る<see cref="DocumentLine"/>が実際に生きている（<c>IsDeleted</c>でない）ことを
/// 「触る前に」確認する寿命チェックを追加する。<c>IsDeleted</c>は例外を投げない安全な
/// プロパティなので、これ自体にコストもリスクも無い。1つでも食い違っていれば、その1フレーム
/// ぶんの描画だけを（例外を投げさせることなく）諦める。既存の<c>catch (InvalidOperationException)</c>
/// （課題#69の対症療法）は、AvaloniaEdit側の未知の内部状態の食い違いに対する最後の安全網として
/// 残すが、根治したのはこちらの事前チェックである。
/// </summary>
public class FoldingReloadLifetimeTests : IDisposable
{
    private readonly ShownWindowTracker _windows = new();
    private readonly string _appDirectory =
        Path.Combine(Path.GetTempPath(), "graft-folding-reload-lifetime", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _windows.Dispose();
        TempDirectoryCleanup.TryDeleteRecursive(_appDirectory);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 折りたたみできる範囲を複数含む、ある程度の行数のPythonふうテキストを生成する。
    /// <paramref name="seed"/>を変えると内容（インデント段数の混ざり方）が変わり、
    /// 「差し替え前後で行数・オフセットが変わる」再読込を模せる。
    /// </summary>
    private static string BuildFoldableText(int blockCount, int seed = 1)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < blockCount; i++)
        {
            sb.Append("if x").Append(i).Append(":\n");
            sb.Append("    a = ").Append(i * seed).Append('\n');
            sb.Append("    b = ").Append(i + seed).Append('\n');
            sb.Append("    print(a, b)\n");
        }
        return sb.ToString();
    }

    private (Window Window, TextEditor Editor) CreateEditorWindow()
    {
        var editor = new TextEditor { Width = 800, Height = 600 };
        var window = _windows.Track(new Window { Width = 800, Height = 600, Content = editor });
        return (window, editor);
    }

    /// <summary>
    /// 回帰テスト（本命・根治の証拠）: <see cref="DocumentSession.ReloadAsync"/>と同じ操作
    /// （同一TextDocumentインスタンスの<c>Text</c>を丸ごと書き換える）の最中、
    /// <c>document.Changed</c>イベント処理の真っ最中に描画が割り込んでも、
    /// <see cref="Editor.IndentGuideRenderer"/>の防御的catch（課題#69。ログへ1回だけ記録して
    /// 描画1フレームぶんを諦める）が発火してはならない。発火した場合はログに
    /// "indent-guide-draw" が残る（<see cref="Editor.IndentGuideRenderer.Draw"/>参照）ため、
    /// これが記録されていないことを持って「事前チェックで未然に防げている」＝根治の証拠とする
    /// （「例外が外へ伝播しない」だけなら課題#69の対症療法でも満たしてしまうため、その一段
    /// 内側まで確認する）。
    /// </summary>
    [AvaloniaFact(DisplayName = "回帰: Changed処理中に描画が割り込んでもインデントガイドの防御的catchが発火しない")]
    public async Task Changed処理中に描画が割り込んでも防御的catchが発火しない()
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var logger = new Logger(appPaths, autoCleanupOnStart: false);

        var (window, editor) = CreateEditorWindow();
        var document = new TextDocument(BuildFoldableText(30));
        editor.Document = document;
        window.Show();

        using var folding = new FoldingSupport(editor);
        folding.Attach(document, "py");
        using var indentGuide = new IndentGuideRenderer(editor, folding) { Logger = logger };

        // 折りたたみ・インデントガイドの初回レイアウト・描画を確定させる。
        using (window.CaptureRenderedFrame()) { }

        // 実機ログの「適用の21ミリ秒後」に相当する状況を直接再現する: DocumentSession.
        // ReloadAsyncの核心（Editor.Documentは差し替えず、同一インスタンスのTextだけを
        // 丸ごと書き換える）が走っている最中、document.Changedイベント処理の真っ最中に
        // 描画が割り込むケースそのものを、実際のDraw()呼び出しで再現する。
        document.Changed += (_, _) =>
        {
            using var rtb = new RenderTargetBitmap(new PixelSize(800, 600));
            using var dc = rtb.CreateDrawingContext();
            indentGuide.Draw(editor.TextArea.TextView, dc);
        };

        var act = () => document.Text = BuildFoldableText(30, seed: 2);
        act.Should().NotThrow("Changed処理中の描画割り込みで例外が外へ伝播してはならない");

        await logger.DisposeAsync();
        var logPath = appPaths.GetLogFilePath(DateOnly.FromDateTime(DateTime.Now));
        var logText = File.Exists(logPath) ? await File.ReadAllTextAsync(logPath) : string.Empty;
        logText.Should().NotContain(
            "indent-guide-draw",
            "防御的catchが発火した（＝食い違いが実際に起きて例外を握りつぶした）ということなので、"
            + "事前チェックで根治できていれば発火しないはず");
    }

    /// <summary>
    /// 回帰テスト: 文書のTextを丸ごと差し替えた直後、通常の描画パス（次のレイアウトパスを
    /// 経た後の再描画）でも例外が出ないこと。適用後の再読込・外部からのファイル変更検知
    /// による再読込の両方がこの経路を通る。
    /// </summary>
    [AvaloniaFact(DisplayName = "回帰: 文書のTextを丸ごと差し替えた直後に描画しても例外が出ない")]
    public void 文書のText差し替え直後の描画で例外が出ない()
    {
        var (window, editor) = CreateEditorWindow();
        var document = new TextDocument(BuildFoldableText(30));
        editor.Document = document;
        window.Show();

        using var folding = new FoldingSupport(editor);
        folding.Attach(document, "py");
        using var indentGuide = new IndentGuideRenderer(editor, folding);

        using (window.CaptureRenderedFrame()) { }

        // DocumentSession.ReloadAsyncの核心部分そのもの。
        document.Text = BuildFoldableText(30, seed: 2);

        var act = () => window.CaptureRenderedFrame()?.Dispose();
        act.Should().NotThrow("文書のTextを丸ごと差し替えた直後に再描画しても落ちてはならない（適用後の再読込を模している）");
    }

    /// <summary>
    /// 回帰テスト: 上と同じ差し替えを、行数が大きく減る方向（折りたたみ範囲そのものが
    /// 無くなる）でも行う。差し替え後に以前の折りたたみ区間が指していたオフセットが
    /// 文書長を超えるケースを踏むため、境界値としても意味がある。
    /// </summary>
    [AvaloniaFact(DisplayName = "回帰: 文書のTextを短い内容へ差し替えた直後に描画しても例外が出ない")]
    public void 文書のText差し替えで短くなっても例外が出ない()
    {
        var (window, editor) = CreateEditorWindow();
        var document = new TextDocument(BuildFoldableText(30));
        editor.Document = document;
        window.Show();

        using var folding = new FoldingSupport(editor);
        folding.Attach(document, "py");
        using var indentGuide = new IndentGuideRenderer(editor, folding);

        using (window.CaptureRenderedFrame()) { }

        document.Text = "print(1)\n"; // 折りたたみ範囲が一切無くなるくらい短い内容へ。

        var act = () => window.CaptureRenderedFrame()?.Dispose();
        act.Should().NotThrow("短い内容への差し替え直後でも落ちてはならない");
    }

    /// <summary>
    /// 回帰テスト: 折りたたみを有効にしたまま外部からファイルが書き換わった場合の経路
    /// （<see cref="Graft.ViewModels.EditorPaneViewModel.NotifyExternalChangeAsync"/>）も、
    /// 中身は<see cref="DocumentSession.ReloadAsync"/>そのもの（同一TextDocumentインスタンスの
    /// Textを丸ごと書き換える）であることを確認したうえで、実際に<see cref="DocumentSession"/>
    /// 経由で再読込しても例外が出ないことを確認する。
    /// </summary>
    [AvaloniaFact(DisplayName = "回帰: 外部変更によるDocumentSession.ReloadAsync経由の再読込でも例外が出ない")]
    public async Task 外部変更によるReloadAsync経由の再読込でも例外が出ない()
    {
        var path = Path.Combine(_appDirectory, $"reload-{Guid.NewGuid():N}.py");
        Directory.CreateDirectory(_appDirectory);
        await File.WriteAllTextAsync(path, BuildFoldableText(30));

        var opened = await DocumentSession.OpenAsync(path, projectRoot: string.Empty);
        opened.IsSuccess.Should().BeTrue();
        using var session = opened.Value;

        var (window, editor) = CreateEditorWindow();
        editor.Document = session.Document;
        window.Show();

        using var folding = new FoldingSupport(editor);
        folding.Attach(session.Document, "py");
        using var indentGuide = new IndentGuideRenderer(editor, folding);

        using (window.CaptureRenderedFrame()) { }

        // 外部（Graft以外の何か、あるいは適用によるファイル書き換え）がディスク上の内容を
        // 変えた状況を模す。
        await File.WriteAllTextAsync(path, BuildFoldableText(30, seed: 2));

        var reloadAct = async () => await session.ReloadAsync();
        await reloadAct.Should().NotThrowAsync("外部変更による再読込そのものが例外を出してはならない");

        var drawAct = () => window.CaptureRenderedFrame()?.Dispose();
        drawAct.Should().NotThrow("外部変更による再読込直後の描画でも例外が出てはならない");
    }

    /// <summary>
    /// 回帰テスト: 連続してタブを切り替えても（＝<c>Editor.Document</c>を複数回差し替えても）
    /// 折りたたみ・インデントガイドの両方で例外が出ないことを確認する（完了条件の1つ。
    /// タブ切替経路自体は課題#60で対策済みだが、今回の変更が退行させていないことの確認）。
    /// </summary>
    [AvaloniaFact(DisplayName = "回帰: 連続したタブ切替でも例外が出ない")]
    public void 連続したタブ切替で例外が出ない()
    {
        var (window, editor) = CreateEditorWindow();
        var docs = Enumerable.Range(0, 5).Select(i => new TextDocument(BuildFoldableText(10, seed: i + 1))).ToList();
        editor.Document = docs[0];
        window.Show();

        using var folding = new FoldingSupport(editor);
        folding.Attach(docs[0], "py");
        using var indentGuide = new IndentGuideRenderer(editor, folding);

        using (window.CaptureRenderedFrame()) { }

        foreach (var doc in docs)
        {
            editor.Document = doc; // FoldingSupportクラスコメント「不具合1」の経路。
            var act1 = () => window.CaptureRenderedFrame()?.Dispose();
            act1.Should().NotThrow("Document差し替え直後の描画で例外が出てはならない");

            folding.Attach(doc, "py");
            var act2 = () => window.CaptureRenderedFrame()?.Dispose();
            act2.Should().NotThrow("Attach後の描画で例外が出てはならない");
        }
    }
}
