using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Folding;
using FluentAssertions;
using Graft.Editor;
using Graft.Features;
using Graft.UiTests.TestSupport;
using Graft.Views;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 実機での指摘（Windows）: 折りたたみマージンのL字線（マーカーから下へ伸びる縦線と終端の
/// 横線）を消す対処（<see cref="MarkerOnlyFoldingMargin"/>への差し替え、
/// <see cref="FoldingSupport"/>の<c>ReplaceFoldingMarginWithMarkerOnly</c>/<c>RemoveCustomMargin</c>）
/// の回帰防止テスト。特に重要なのは「解除（Uninstall）で自前のマージンを取り除き忘れない」
/// （<see cref="FoldingManager.Uninstall"/>は差し替え前の標準<see cref="FoldingMargin"/>インスタンス
/// しか対象にしないため、対処しないと<c>LeftMargins</c>に残り続ける・増殖する）こと。
///
/// GitGutterProviderやShowLineNumbersが追加するLineNumberMargin/DottedLineMarginなど
/// 他のマージンと並べたうえで検証し、実際のEditorPaneの構成（EditorPane.axaml.cs参照:
/// ShowLineNumbers=true → FoldingSupport.Attach → GitGutterProviderをindex 0へInsert）を
/// なるべく再現する。
///
/// 実機での指摘2（Windows）: ＋/－マーカーにマウスを合わせてもIビームのままという指摘の
/// 回帰防止テストも本ファイルに含む。調査（下記コメント）の結果、マーカー自体のヒットテスト
/// ・クリックでの折りたたみ操作は正常に機能しており、原因はカーソルの継承
/// （<see cref="MarkerOnlyFoldingMargin"/>のクラスコメント参照）だった。
///
/// 【調査方法と実測結果】 window.GetVisualAt(...)で折りたたみマーカーの中心座標に対する
/// ヒットテストを行うと、AvaloniaEdit内部の<c>FoldingMarginMarker</c>自身が返り
/// （マーカーは実際にポインタイベントを受け取れている）、続けてheadlessのMouseDown/MouseUpで
/// 同じ座標をクリックすると<c>FoldingSection.IsFolded</c>がfalse→trueへ実際に反転した
/// （＋/－マーカーをクリックして折りたたむ操作自体は壊れていない）。一方、エディタ本文を
/// クリックしたあとは折りたたみマージン・Gitガター・行番号マージンのいずれも
/// <c>Cursor</c>の実効値が"Ibeam"になっており、これが実機の症状と一致した。さらに、
/// <c>FoldingMargin.OnTextViewVisualLinesChanged</c>は可視行が変わるたび（今回のクリックに
/// よる折りたたみも含む）<c>FoldingMarginMarker</c>を全部作り直すため、マウスを動かさずに
/// 可視行だけが変わると「Cursorのローカル値をまだ持たない新しいマーカー」に入れ替わり、
/// 次にマウスが実際に動くまでIビームのまま取り残されることも確認した
/// （このマーカーのインスタンス作り直し自体はテスト対象外の既存挙動）。
/// </summary>
public class FoldingMarginTests : IDisposable
{
    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        _windows.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>実際のEditorPaneと同じ順序でマージンを組み立てる
    /// （行番号→GitGutterをindex 0へ挿入→折りたたみをAttach）。</summary>
    private (Window Window, TextEditor Editor, GitGutterProvider GitGutter) CreateEditorWithMargins()
    {
        var editor = new TextEditor { Width = 800, Height = 600, ShowLineNumbers = true };
        var window = _windows.Track(new Window { Width = 800, Height = 600, Content = editor });

        var gitGutter = new GitGutterProvider(editor, new GitIntegration());
        editor.TextArea.LeftMargins.Insert(0, gitGutter); // EditorPane.axaml.csと同じ挿入位置。

        return (window, editor, gitGutter);
    }

    [AvaloniaFact(DisplayName = "折りたたみ有効時、LeftMargins上の折りたたみマージンはMarkerOnlyFoldingMarginである")]
    public void 折りたたみマージンはMarkerOnlyFoldingMarginに差し替わっている()
    {
        var (_, editor, _) = CreateEditorWithMargins();
        var document = new TextDocument("void Foo()\n{\n    Bar();\n}\n");
        editor.Document = document;

        using var folding = new FoldingSupport(editor);
        folding.Attach(document, ".cs");

        var foldingMargins = editor.TextArea.LeftMargins.OfType<FoldingMargin>().ToList();
        foldingMargins.Should().HaveCount(1, "折りたたみマージンはちょうど1つだけ存在するはず");
        foldingMargins[0].Should().BeOfType<MarkerOnlyFoldingMargin>(
            "L字線を描かないよう差し替えた自前のマージンであるはず（標準のFoldingMarginのままではない）");
    }

    [AvaloniaFact(DisplayName = "折りたたみマージンへの差し替えでも他のマージンとの並び順は変わらない")]
    public void マージンの並び順が差し替え前後で変わらない()
    {
        var (_, editor, gitGutter) = CreateEditorWithMargins();
        var document = new TextDocument("void Foo()\n{\n    Bar();\n}\n");
        editor.Document = document;

        // Attach前: [GitGutter, LineNumberMargin, DottedLineMargin]（ShowLineNumbers=trueが
        // TextEditor側で自動的に挿入する2つ。標準のFoldingMarginはまだ存在しない）。
        var beforeTypes = editor.TextArea.LeftMargins.Select(m => m.GetType().Name).ToList();
        editor.TextArea.LeftMargins[0].Should().BeSameAs(gitGutter, "GitGutterは常に先頭に挿入される");
        beforeTypes.Should().NotContain("FoldingMargin", "Attach前は折りたたみマージンがまだ存在しない");

        using var folding = new FoldingSupport(editor);
        folding.Attach(document, ".cs");

        var afterMargins = editor.TextArea.LeftMargins;

        // 差し替え後もGitGutter・LineNumberMargin・DottedLineMarginの相対位置（先頭3つ）は
        // 一切変わらず、末尾（標準のFoldingMarginが元々追加されていたのと同じインデックス）に
        // MarkerOnlyFoldingMarginが1つ増えているだけのはず。末尾へ「追加し直す」実装だと
        // ここが崩れないが、万一先頭へ挿入する・並び順を並べ替えるような実装変更が入った場合に
        // 検知できるよう、先頭3つの型名と参照の両方を確認する。
        afterMargins.Count.Should().Be(beforeTypes.Count + 1,
            "折りたたみマージンが1つ増えるだけで、他のマージンが増減してはいけない");
        for (var i = 0; i < beforeTypes.Count; i++)
        {
            afterMargins[i].GetType().Name.Should().Be(beforeTypes[i],
                $"index {i} のマージン種別は差し替えの前後で変わらないはず");
        }
        afterMargins[0].Should().BeSameAs(gitGutter, "GitGutter自体のインスタンス・位置も変わらないはず");
        afterMargins[^1].Should().BeOfType<MarkerOnlyFoldingMargin>(
            "標準のFoldingMarginが追加されるのと同じ末尾の位置に、差し替え後のマージンがあるはず");
    }

    [AvaloniaFact(DisplayName = "折りたたみを無効化するとLeftMarginsに折りたたみマージンが1つも残らない")]
    public void 無効化後は折りたたみマージンが残らない()
    {
        var (_, editor, _) = CreateEditorWithMargins();
        var document = new TextDocument("void Foo()\n{\n    Bar();\n}\n");
        editor.Document = document;

        using var folding = new FoldingSupport(editor);
        folding.Attach(document, ".cs");
        editor.TextArea.LeftMargins.OfType<FoldingMargin>().Should().HaveCount(1, "前提: 有効化直後は1つ存在する");

        folding.SetEnabled(false);

        editor.TextArea.LeftMargins.OfType<FoldingMargin>().Should().BeEmpty(
            "FoldingManager.Uninstallは差し替え前の標準FoldingMarginしか対象にしないため、" +
            "自前のMarkerOnlyFoldingMarginを明示的に取り除かないとここに残ってしまう（回帰防止の本命）");
    }

    [AvaloniaFact(DisplayName = "文書を差し替えて解除してもLeftMarginsに折りたたみマージンが残らない")]
    public void 文書差し替えによる解除後も折りたたみマージンが残らない()
    {
        var (_, editor, _) = CreateEditorWithMargins();
        var document1 = new TextDocument("void Foo()\n{\n    Bar();\n}\n");
        editor.Document = document1;

        using var folding = new FoldingSupport(editor);
        folding.Attach(document1, ".cs");
        editor.TextArea.LeftMargins.OfType<FoldingMargin>().Should().HaveCount(1);

        // タブ切替相当: Editor.Documentの差し替え → Attachの呼び直し
        // （FoldingSupportクラスコメントの「不具合1」参照。DocumentChangedで同期的にUninstallされる）。
        var document2 = new TextDocument("def foo():\n    pass\n");
        editor.Document = document2;
        folding.Attach(document2, ".py");

        editor.TextArea.LeftMargins.OfType<FoldingMargin>().Should().HaveCount(1,
            "文書差し替え後も折りたたみマージンは新しい文書向けに1つだけ存在し、古いものが残ってはいけない");

        // FoldingSupport自体をDispose（最終的な解除経路）した後は1つも残らない。
        folding.Dispose();
        editor.TextArea.LeftMargins.OfType<FoldingMargin>().Should().BeEmpty(
            "Disposeによる最終的な解除後は折りたたみマージンが1つも残らないはず");
    }

    [AvaloniaFact(DisplayName = "有効→無効→有効を繰り返しても折りたたみマージンは増殖しない")]
    public void 有効無効の繰り返しでマージンが増殖しない()
    {
        var (_, editor, _) = CreateEditorWithMargins();
        var document = new TextDocument("void Foo()\n{\n    Bar();\n}\n");
        editor.Document = document;

        using var folding = new FoldingSupport(editor);
        folding.Attach(document, ".cs");

        for (var i = 0; i < 5; i++)
        {
            folding.SetEnabled(false);
            editor.TextArea.LeftMargins.OfType<FoldingMargin>().Should().BeEmpty(
                $"{i}回目の無効化直後は折りたたみマージンが残っていないはず");

            folding.SetEnabled(true);
            var margins = editor.TextArea.LeftMargins.OfType<FoldingMargin>().ToList();
            margins.Should().HaveCount(1, $"{i}回目の再有効化後も折りたたみマージンはちょうど1つのはず（増殖しない）");
            margins[0].Should().BeOfType<MarkerOnlyFoldingMargin>();
        }
    }

    /// <summary>折りたたみ可能な範囲を持つ文書を組み立て、ウィンドウを表示してから
    /// 折りたたみマーカー（内部クラス<c>FoldingMarginMarker</c>）を1つ見つけて返す。
    /// クラスコメントの「調査方法」節と同じ手順（GetVisualChildren→型名で絞り込み）。</summary>
    private (Window Window, TextEditor Editor, FoldingSupport Folding, FoldingMargin Margin, Control Marker)
        CreateEditorWithVisibleMarker()
    {
        var (window, editor, _) = CreateEditorWithMargins();
        var document = new TextDocument("void Foo()\n{\n    Bar();\n}\n");
        editor.Document = document;

        var folding = new FoldingSupport(editor);
        folding.Attach(document, ".cs"); // 括弧ベース戦略。2行目の"{"から折りたたみ範囲が1つできる。

        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull(); // レイアウト確定（マーカー生成に必要）。

        var margin = editor.TextArea.LeftMargins.OfType<FoldingMargin>().Single();
        var marker = margin.GetVisualChildren().FirstOrDefault(v => v.GetType().Name == "FoldingMarginMarker") as Control;
        marker.Should().NotBeNull("折りたたみ範囲が1つあるので、マーカーが最低1つ生成されているはず");

        return (window, editor, folding, margin, marker!);
    }

    [AvaloniaFact(DisplayName = "折りたたみマーカーは実際にポインタイベントを受け取れる（ヒットテストで自分自身が返る）")]
    public void マーカーはヒットテストで自分自身が返る()
    {
        var (window, _, folding, _, marker) = CreateEditorWithVisibleMarker();
        using var _1 = folding;

        // クラスコメント「まず確かめてほしいこと」への回答: マーカー自身の中心座標をウィンドウ
        // 座標へ変換し、window.GetVisualAt(...)（実際の入力パイプラインと同じヒットテスト）で
        // 何が返るかを確認する。座標はスクリーンショットからの目測ではなく、実際のBoundsから
        // 計算する。
        var centerInMarker = new Point(marker.Bounds.Width / 2, marker.Bounds.Height / 2);
        var pointInWindow = ((Visual)marker).TranslatePoint(centerInMarker, window);
        pointInWindow.Should().NotBeNull("マーカーがウィンドウ内に配置されていること（レイアウト確定後）");

        var hit = window.GetVisualAt(pointInWindow!.Value);
        hit.Should().BeSameAs(marker,
            "マーカーの中心座標でのヒットテストは、他の何か（親のマージン等）ではなくマーカー自身を返すはず。" +
            "これが崩れる＝マーカーがポインタイベントを受け取れなくなる（クリックでの折りたたみも壊れる）");
    }

    [AvaloniaFact(DisplayName = "折りたたみマーカーをクリックすると実際に折りたたまれる（IsFolded反転）")]
    public void マーカーをクリックすると折りたたまれる()
    {
        var (window, _, folding, _, marker) = CreateEditorWithVisibleMarker();
        using var _1 = folding;

        var centerInMarker = new Point(marker.Bounds.Width / 2, marker.Bounds.Height / 2);
        var pointInWindow = ((Visual)marker).TranslatePoint(centerInMarker, window)!.Value;

        var section = folding.Manager!.AllFoldings.First();
        section.IsFolded.Should().BeFalse("前提: 初期状態では展開されている");

        // 実際のマウス操作（Move→Down→Up）でクリックを模擬する。AvaloniaEdit内部の
        // FoldingMarginMarker.OnPointerPressedがIsExpandedを反転させ、それがFoldingSection.
        // IsFoldedへ反映される経路をAPI越しではなく実際の入力で確認する。
        window.MouseMove(pointInWindow);
        window.MouseDown(pointInWindow, MouseButton.Left);
        window.MouseUp(pointInWindow, MouseButton.Left);

        section.IsFolded.Should().BeTrue(
            "＋/－マーカーをクリックして折りたたむ操作自体は壊れていないはず（クラスコメントの実測結果参照）");
    }

    [AvaloniaFact(DisplayName = "実機での指摘2: 本文クリックでIビームになった後でも折りたたみマージンは矢印カーソルのまま")]
    public void 本文クリック後も折りたたみマージンは矢印カーソル()
    {
        var (window, editor, folding, margin, marker) = CreateEditorWithVisibleMarker();
        using var _1 = folding;

        ClickInsideTextView(window, editor);

        // 注意: 「x?.ToString().Should().Be(...)」という書き方はxがnullだとShould()以降ごと
        // 短絡されアサーション自体が実行されない（黙って成功扱いになる）ため、必ず文字列化した
        // 値を一度ローカル変数へ入れてからShould()を呼ぶ（この節すべてで同じ配慮をしている）。
        var textAreaCursor = editor.TextArea.Cursor?.ToString() ?? "null";
        textAreaCursor.Should().Be("Ibeam",
            "前提: AvaloniaEditのSelectionMouseHandlerが本文クリックでTextArea.CursorへIビームを" +
            "ローカル値として設定するはず（この前提が崩れたら以下の検証自体が無意味になる）");

        var marginCursor = margin.Cursor?.ToString() ?? "null";
        marginCursor.Should().Be("Arrow",
            "MarkerOnlyFoldingMarginのコンストラクタで矢印をローカル値に設定しているため、" +
            "TextAreaからIビームを継承してはいけない");
        // マーカー自身はCursorのローカル値を明示的に設定していない（ホバーで初めて自分の値を
        // 持つ）ため、.Cursorの実効値はここでもマージン側のローカル値を継承しているはず。
        var markerCursor = marker.Cursor?.ToString() ?? "null";
        markerCursor.Should().Be("Arrow",
            "マーカーは自分のCursorローカル値を持たない限り、親であるマージンから矢印を継承するはず" +
            "（マージン側で直しておくことで、作り直された直後のマーカーでもマウスを動かさず矢印になる）");
    }

    [AvaloniaFact(DisplayName = "実機での指摘2: 本文クリックでIビームになった後でもGitガターは矢印カーソルのまま")]
    public void 本文クリック後もGitガターは矢印カーソル()
    {
        var (window, editor, gitGutter) = CreateEditorWithMargins();
        editor.Document = new TextDocument("a\nb\nc\n");
        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull();

        ClickInsideTextView(window, editor);

        var textAreaCursor = editor.TextArea.Cursor?.ToString() ?? "null";
        textAreaCursor.Should().Be("Ibeam", "前提: 本文クリックでIビームになっているはず");
        var gitGutterCursor = gitGutter.Cursor?.ToString() ?? "null";
        gitGutterCursor.Should().Be("Arrow",
            "GitGutterProviderのコンストラクタで矢印をローカル値に設定しているため、" +
            "TextAreaからIビームを継承してはいけない");
    }

    [AvaloniaFact(DisplayName = "実機での指摘2: 本文クリックでIビームになった後でも行番号マージンは矢印カーソルのまま")]
    public void 本文クリック後も行番号マージンは矢印カーソル()
    {
        // LineNumberMarginはFoldingMargin/GitGutterProviderと違い、AvaloniaEdit内部
        // （非公開のTextEditor.OnShowLineNumbersChanged）がShowLineNumbers切り替えのたび
        // 作り直す。その作り直しに追従する購読（EditorPane.axaml.csのOnLeftMarginsChanged）は
        // EditorPane側にしか無いため、単体のTextEditorではなく実物のEditorPaneを使って検証する
        // （EditorPane.axaml.csのShowLineNumbers="{Binding ShowLineNumbers}"はDataContext未設定の
        // ここでは解決されないため、実アプリの設定バインディングと同じ入り口である
        // Editor.ShowLineNumbersへ直接trueを立てて、AvaloniaEdit側の生成契機を再現する）。
        var pane = new EditorPane();
        var window = _windows.Track(new Window { Width = 800, Height = 600, Content = pane });
        window.Show();

        pane.Editor.Document = new TextDocument("a\nb\nc\n");
        pane.Editor.ShowLineNumbers = true;
        // EditorPaneのコンストラクタはEditor.IsEnabled=falseで始まり、実際のタブ読み込み
        // （ApplyDocumentTab）がtrueへ戻す。ここではタブ読み込みの全経路を再現しないため、
        // 無効のままだとポインタ入力を一切受け付けずクリックが素通りしてしまう。
        pane.Editor.IsEnabled = true;
        window.CaptureRenderedFrame().Should().NotBeNull();

        var lineNumberMargin = pane.Editor.TextArea.LeftMargins.OfType<LineNumberMargin>().Single();

        ClickInsideTextView(window, pane.Editor);

        var cursorStr = pane.Editor.TextArea.Cursor?.ToString() ?? "null";
        var marginCursorStr = lineNumberMargin.Cursor?.ToString() ?? "null";

        cursorStr.Should().Be("Ibeam", "前提: 本文クリックでIビームになっているはず");
        marginCursorStr.Should().Be("Arrow",
            "EditorPane.OnLeftMarginsChangedが矢印をローカル値に設定しているため、" +
            "TextAreaからIビームを継承してはいけない（この購読が無いと行番号マージンだけIビームのまま残る）");
    }

    /// <summary>エディタ本文（<c>TextArea.TextView</c>、マージンより右側）をクリックし、
    /// AvaloniaEditのSelectionMouseHandlerに<c>TextArea.Cursor = Cursor.Parse("IBeam")</c>を
    /// 発火させる。座標を決め打ちせず<c>TextView.Bounds</c>の中心（マージンを含まない本文だけの
    /// 座標系）から計算することで、EditorPaneの実レイアウト（タブバー・ステータスバー等の
    /// 有無で本文の位置・大きさが変わる）に依存せず確実に本文へ当てる。</summary>
    private static void ClickInsideTextView(Window window, TextEditor editor)
    {
        var textView = editor.TextArea.TextView;
        var centerInTextView = new Point(textView.Bounds.Width / 2, textView.Bounds.Height / 2);
        var textPoint = ((Visual)textView).TranslatePoint(centerInTextView, window)!.Value;
        window.MouseMove(textPoint);
        window.MouseDown(textPoint, MouseButton.Left);
        window.MouseUp(textPoint, MouseButton.Left);
    }
}
