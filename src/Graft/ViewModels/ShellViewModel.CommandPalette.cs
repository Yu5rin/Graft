using System.Windows.Input;
using Graft.Features;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="ShellViewModel"/> の分割ファイル（1ファイル400行上限のため）。
///
/// コマンドパレット（Ctrl+Shift+P、機能改善）を担う。既存のクイックオープン
/// （<see cref="QuickOpenViewModel"/>、Ctrl+P）と同じ操作感・実装作法に揃えるため、
/// このファイルもQuickOpen関連のShellViewModel側の配線（ToggleQuickOpenCommand等）と
/// 対になる構造にしている。
///
/// 一覧に載せる操作は、ツールバー・サイドバー・ショートカットで既に到達できる既存の
/// <see cref="ICommand"/>をそのまま集めたもの（新しいコマンドは作らない。仕様どおり）。
/// ショートカット表記は<see cref="ShortcutCatalog"/>から逆引きし、
/// <c>Graft.Views.ShortcutsWindow</c>の一覧と二重管理にならないようにする
/// （ShortcutCatalog.csのクラスコメント参照）。
/// </summary>
public sealed partial class ShellViewModel
{
    /// <summary>コマンドパレット本体。</summary>
    public CommandPaletteViewModel CommandPalette { get; private set; } = null!;

    /// <summary>Ctrl+Shift+P。コマンドパレットの開閉（トグル）。</summary>
    public ICommand ToggleCommandPaletteCommand { get; private set; } = null!;

    /// <summary>
    /// コンストラクタ終端から呼ぶ（Graft・Editor・他のICommandが一通り組み上がった後で
    /// ないと、対象コマンドを集められないため）。
    /// </summary>
    private void InitializeCommandPalette()
    {
        CommandPalette = new CommandPaletteViewModel(BuildPaletteCommands());
        ToggleCommandPaletteCommand = new RelayCommand(ToggleCommandPalette);
    }

    /// <summary>
    /// クイックオープンが開いていれば先に閉じてから開閉する（ToggleQuickOpenAsyncの
    /// 逆側の配線。2つのオーバーレイが同時に重なって表示されないようにするため）。
    /// </summary>
    private void ToggleCommandPalette()
    {
        if (QuickOpen.IsOpen) QuickOpen.Close();
        CommandPalette.Toggle();
    }

    /// <summary>
    /// パレットに載せる操作の一覧。ツールバー・接ぎ木パネルの見出し・右クリックメニュー・サイドバー・
    /// ショートカットのどれかで到達できる操作は、すべてここから実行できる（プロジェクト番号切り替え
    /// （Ctrl+Alt+1〜9）のように、対象が状況依存で単一の固定操作として表現しづらいものと、エディタ本文の
    /// 編集キー（行の移動・複製・コメント切り替え・補完）は対象外）。
    /// 分類（<see cref="PaletteCategories"/>）ごとに、よく使う順に並べる。各行の左に分類名、右にキーを出す。
    /// 操作名は、画面のボタン・メニューと同じ言い方に揃える（「依頼文をコピー」「貼り付けて解析」など）。
    /// </summary>
    private List<PaletteCommandDescriptor> BuildPaletteCommands()
    {
        string? Gesture(string key) => ShortcutCatalog.GestureFor(key);

        // エディタ内の操作（検索・置換・行へ移動・タブを閉じる）は、キー操作と同じ経路（ShellWindow が
        // EditorPane へ同じキーを渡す）で実行する。パレットだけ別の実装にすると、食い違いが出るため。
        ICommand EditorKey(string name, Func<bool> canExecute)
            => new RelayCommand(() => RequestEditorShortcut?.Invoke(this, name), canExecute);
        bool HasDocumentTab() => Editor.ActiveTab is { Kind: EditorTabKind.Document };

        var c = PaletteCategories.File;
        var list = new List<PaletteCommandDescriptor>
        {
            // ===== ファイル =====
            new("ファイル名であいまい検索して開く（クイックオープン）", ToggleQuickOpenCommand, null, Gesture("QuickOpen"), c),
            new("現在のファイルを保存", new RelayCommand(() => _ = Editor.SaveActiveAsync(), HasDocumentTab), null, Gesture("Save"), c),
            new("開いているファイルをすべて保存", new RelayCommand(() => _ = Editor.SaveAllAsync(), () => Editor.Tabs.Count > 0), null, Gesture("SaveAll"), c),
            new("現在のタブを閉じる", EditorKey("CloseTab", () => Editor.ActiveTab is not null), null, Gesture("CloseTab"), c),
            new("直前に閉じたタブを開き直す", Editor.ReopenLastClosedTabCommand, null, Gesture("ReopenLastClosedTab"), c),
            new("新規ファイルを作る", Explorer.NewFileCommand, null, null, c),
            new("新規フォルダを作る", Explorer.NewFolderCommand, null, null, c),
            new("既存のファイルをプロジェクトに追加", Explorer.AddFileCommand, null, null, c),
            new("削除したファイルを元に戻す", Explorer.UndoDeleteCommand, null, Gesture("UndoDelete"), c),
            new("ファイル一覧を更新", Explorer.RefreshCommand, null, null, c),
            new("プロジェクトを追加", Graft.ProjectPane.AddProjectCommand, null, null, c),
            // プロジェクト一覧の右クリックメニュー「パスをコピー」「エクスプローラーで表示」と同じ操作。
            // 後者の名前はOSで変わるため、メニューと同じ共通の言い方（MenuText）から作る。
            new("選択中のプロジェクトのパスをコピー", Graft.ProjectPane.CopyPathCommand, null, null, c),
            new("選択中のプロジェクトを" + Views.MenuText.RevealInFileManager, Graft.ProjectPane.RevealCommand, null, null, c),

            // ===== 編集 =====
            new("ファイル内を検索", EditorKey("Find", HasDocumentTab), null, Gesture("Find"), PaletteCategories.Edit),
            new("ファイル内を置換", EditorKey("Replace", HasDocumentTab), null, Gesture("Replace"), PaletteCategories.Edit),
            new("行番号を指定して移動", EditorKey("GoToLine", HasDocumentTab), null, Gesture("GoToLine"), PaletteCategories.Edit),
            // 検討書「折りたたみの機能追加」(b) 折りたたみコマンド。ShortcutCatalogはレベル1〜5をまとめて
            // 1行（"Ctrl+Shift+1〜5"）にしか持たないため、レベル2〜5はGesture("FoldLevel1")を流用せず
            // リテラル表記を直接渡す（実際のキー割り当てはEditorPane.axaml.csのTryHandleFoldShortcutsと一致させている）。
            new("折りたたみ: レベル1で畳む", Editor.FoldLevel1Command, null, Gesture("FoldLevel1"), PaletteCategories.Edit),
            new("折りたたみ: レベル2で畳む", Editor.FoldLevel2Command, null, "Ctrl+Shift+2", PaletteCategories.Edit),
            new("折りたたみ: レベル3で畳む", Editor.FoldLevel3Command, null, "Ctrl+Shift+3", PaletteCategories.Edit),
            new("折りたたみ: レベル4で畳む", Editor.FoldLevel4Command, null, "Ctrl+Shift+4", PaletteCategories.Edit),
            new("折りたたみ: レベル5で畳む", Editor.FoldLevel5Command, null, "Ctrl+Shift+5", PaletteCategories.Edit),
            new("折りたたみ: すべてのコメントブロックを畳む", Editor.FoldAllCommentsCommand, null, Gesture("FoldAllComments"), PaletteCategories.Edit),
            new("折りたたみ: カーソル位置を再帰的に畳む", Editor.FoldRecursiveCommand, null, Gesture("FoldRecursive"), PaletteCategories.Edit),

            // ===== 表示 =====
            new("プロジェクトビューに切り替え", ShowSideViewCommand, SideViewKind.Project, Gesture("SelectProject"), PaletteCategories.View),
            new("エクスプローラビューに切り替え", ShowSideViewCommand, SideViewKind.Explorer, Gesture("SelectExplorer"), PaletteCategories.View),
            new("検索ビューに切り替え（プロジェクト全体を検索）", ShowSideViewCommand, SideViewKind.Search, Gesture("SelectSearch"), PaletteCategories.View),
            new("接ぎ木パネルを開閉", ToggleGraftPanelCommand, null, Gesture("ToggleGraftPanel"), PaletteCategories.View),
            new("接ぎ木パネルの配置を切り替え（下／右）", ToggleGraftPanelPlacementCommand, null, Gesture("ToggleGraftPanelPlacement"), PaletteCategories.View),

            // ===== AIに渡す =====
            new("AIに渡すファイルを選ぶ", Graft.OpenContextCollectCommand, null, Gesture("OpenContextCollect"), PaletteCategories.Handoff),
            new("依頼文をコピー", Graft.CopyPromptCommand, null, Gesture("CopyPrompt"), PaletteCategories.Handoff),
            new("依頼文のテンプレートを選んでコピー", Graft.OpenPromptDropdownCommand, null, Gesture("OpenPromptDropdown"), PaletteCategories.Handoff),

            // ===== AIから受け取る =====
            new("貼り付けて解析（クリップボードの回答を解析）", Graft.PasteAndParseCommand, null, Gesture("PasteAndParse"), PaletteCategories.Receive),
            // 実機で確認された指摘2: 「ファイルから解析」への到達手段が接ぎ木パネルの空状態ボタンしか無く、
            // ショートカットも無かった。専用のショートカットは既存の割り当てとぶつかる恐れがあるため新設せず、
            // パレット登録のみで到達手段を確保する（Gestureはnull＝バッジ非表示）。
            new("ファイルから解析", Graft.ParseFromFileCommand, null, Gesture("ParseFromFile"), PaletteCategories.Receive),
            new("適用", Graft.ApplyCommand, null, Gesture("Apply"), PaletteCategories.Receive),
            new("再チェック（適用できるかをもう一度調べる）", Graft.PreviewCommand, null, Gesture("Recheck"), PaletteCategories.Receive),
            new("キューへ追加", Graft.AddCurrentPatchToQueueCommand, null, Gesture("AddToQueue"), PaletteCategories.Receive),
            new("キューを開く", Graft.OpenQueueCommand, null, Gesture("OpenQueue"), PaletteCategories.Receive),
            new("修正を依頼（失敗した変更の修正依頼文をコピー）", Graft.CopyRecoveryPromptCommand, null, Gesture("CopyRecoveryPrompt"), PaletteCategories.Receive),
            new("解析結果を破棄", Graft.DiscardCommand, null, Gesture("Discard"), PaletteCategories.Receive),
            new("クリップボード監視を一時停止／再開", ToggleClipboardWatchPauseCommand, null, Gesture("ToggleClipboardWatch"), PaletteCategories.Receive),

            // ===== 履歴 =====
            new("履歴を開く", Graft.ShowHistoryCommand, null, Gesture("ShowHistory"), PaletteCategories.History),
            new("直前の適用を取り消す", Graft.UndoCommand, null, Gesture("Undo"), PaletteCategories.History),
            // 実機で確認された指摘8: 履歴の「この適用を取り消す」「ここまで戻す」は、ショートカットが無く
            // 右クリックメニューかラベルの無いアイコンボタンのみが到達手段で、キーボードだけでは実行できなかった。
            // ショートカットの新設は既存の割り当てとぶつかる恐れがあるため見送り、パレット登録を優先する。
            // どちらも履歴ペインで選択中の履歴（HistoryPaneViewModel.SelectedItem）に対して動くコマンドで、
            // パラメータは不要（CanExecuteは選択の有無で決まる。選択が無ければパレット上は「実行できません」と表示される）。
            new("選択中の適用を取り消す", Graft.History.RestoreCommand, null, Gesture("RestoreRevision"), PaletteCategories.History),
            new("選択中の履歴までまとめて戻す", Graft.History.RestoreThroughCommand, null, Gesture("RestoreThrough"), PaletteCategories.History),
            new("選択中の履歴の変更を見る", Graft.History.ShowDiffCommand, null, null, PaletteCategories.History),
            new("選択中の履歴の要約をコピー", Graft.History.CopySummaryCommand, null, null, PaletteCategories.History),
            new("選択中の履歴のバックアップフォルダを開く", Graft.History.OpenBackupFolderCommand, null, null, PaletteCategories.History),

            // ===== 設定とヘルプ =====
            new("設定を開く", Graft.OpenSettingsCommand, null, Gesture("OpenSettings"), PaletteCategories.Help),
            new("キーボードショートカット一覧を開く", OpenShortcutsCommand, null, Gesture("OpenShortcuts"), PaletteCategories.Help),
            new("取扱説明書を開く", OpenManualCommand, null, Gesture("OpenManual"), PaletteCategories.Help),
            // 画面上のチュートリアル（コーチマーク）。専用のキー割り当ては無いため、Gestureは常にnull。
            new("使い方を学ぶ（画面上のチュートリアル）", StartTutorialCommand, null, Gesture("StartTutorial"), PaletteCategories.Help),
        };
        return list;
    }

    /// <summary>
    /// コマンドパレットからの、エディタ内の操作（<c>Find</c>・<c>Replace</c>・<c>GoToLine</c>・<c>CloseTab</c>）の要求。
    /// ShellWindow が購読し、キー操作（Ctrl+F・Ctrl+H・Ctrl+G・Ctrl+W）と同じ経路でエディタ領域へ渡す。
    /// </summary>
    public event EventHandler<string>? RequestEditorShortcut;
}
