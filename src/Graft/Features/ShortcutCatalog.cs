namespace Graft.Features;

/// <summary>
/// 1件のキーボードショートカット（<c>Graft.Views.ShortcutsWindow</c>の一覧の1行に対応する）。
/// <see cref="CommandKey"/>はコマンドパレット（Ctrl+Shift+P）がショートカット表記を
/// 逆引きするための任意の識別子。ShellViewModelが構築するコマンド一覧の識別子と
/// 一致させる（対応するICommandを持たない項目はnullのままでよい）。
/// </summary>
public sealed record ShortcutEntry(string Category, string Gesture, string Description, string? CommandKey = null);

/// <summary>
/// キーボードショートカットの唯一の一覧（機能改善: コマンドパレット追加に伴い新設）。
///
/// 【二重管理を避けるための設計】ショートカット一覧ウィンドウ（<see cref="Views.ShortcutsWindow"/>）は
/// 従来、機能分類ごとのキー表記・説明文を静的なXAMLとして直接書いていた。コマンドパレットの
/// 各項目にもショートカット表記を出す必要があるが、XAML中の文字列を別途コードから読み取る
/// 手段は無いため、そのまま2箇所に同じ文字列を書くと二重管理になってしまう。
/// そこでキー表記・説明文をこのクラスへ一元化し、
/// (1) Graft.Views.ShortcutsWindow.axaml.cs はここから読み取って一覧を組み立て、
/// (2) ShellViewModel（コマンドパレットの項目一覧を構築する側）は<see cref="GestureFor"/>で
///     コマンドごとのキー表記を逆引きする。
/// 単一の情報源（このファイル）を変更すれば両方に反映される。
/// </summary>
public static class ShortcutCatalog
{
    /// <summary>
    /// 機能分類→掲載順。ShellWindow.Keyboard.csに実在するショートカットのみを載せる
    /// （元のShortcutsWindow.axamlの内容をそのまま移した）。
    /// </summary>
    public static IReadOnlyList<ShortcutEntry> Entries { get; } = new[]
    {
        // ============== 接ぎ木の操作 ==============
        new ShortcutEntry("接ぎ木の操作", "Ctrl+Shift+V",
            "クリップボードのAIの回答を読み取って解析します。接ぎ木の入口です。", "PasteAndParse"),
        new ShortcutEntry("接ぎ木の操作", "Ctrl+Enter",
            "解析した変更を実際のファイルへ適用します。", "Apply"),
        new ShortcutEntry("接ぎ木の操作", "Ctrl+Alt+Z",
            "直前に適用したリビジョンを元に戻します。", "Undo"),
        new ShortcutEntry("接ぎ木の操作", "Ctrl+J",
            "接ぎ木パネル（下部）を開閉します。", "ToggleGraftPanel"),
        new ShortcutEntry("接ぎ木の操作", "Ctrl+Shift+C",
            "AIへの依頼文（プロンプトテンプレート）をコピーします。", "CopyPrompt"),
        new ShortcutEntry("接ぎ木の操作", "Ctrl+Alt+1〜9",
            "登録したプロジェクトを番号で切り替えます。"),
        new ShortcutEntry("接ぎ木の操作", "Space",
            "（ブロック一覧にフォーカス中）選択したブロックの適用チェックを切り替えます。"),
        new ShortcutEntry("接ぎ木の操作", "Escape",
            "解析中のパッチを破棄し、ブロック一覧を空にします。", "Discard"),

        // ============== ファイル・エディタ ==============
        new ShortcutEntry("ファイル・エディタ", "Ctrl+P",
            "ファイル名であいまい検索して開きます（クイックオープン）。", "QuickOpen"),
        new ShortcutEntry("ファイル・エディタ", "Ctrl+S",
            "現在のタブを保存します。"),
        new ShortcutEntry("ファイル・エディタ", "Ctrl+Shift+S",
            "開いているタブをすべて保存します。"),
        new ShortcutEntry("ファイル・エディタ", "Ctrl+W",
            "現在のタブを閉じます（未保存なら保存確認あり）。"),
        new ShortcutEntry("ファイル・エディタ", "Ctrl+Shift+T",
            "直前に閉じたタブを、カーソル位置ごと開き直します（最大10件、新しい順）。", "ReopenLastClosedTab"),
        new ShortcutEntry("ファイル・エディタ", "Ctrl+Tab",
            "直近使用した順にタブを切り替えます。"),
        new ShortcutEntry("ファイル・エディタ", "Ctrl+G",
            "指定した行番号へ移動します。"),
        new ShortcutEntry("ファイル・エディタ", "Ctrl+/",
            "（ファイル編集中）現在行のコメントを切り替えます。"),
        new ShortcutEntry("ファイル・エディタ", "Alt+↑ / Alt+↓",
            "現在行を上/下の行と入れ替えます。"),
        new ShortcutEntry("ファイル・エディタ", "Shift+Alt+↓",
            "現在行を複製して直下に挿入します。"),
        new ShortcutEntry("ファイル・エディタ", "Ctrl+Shift+K",
            "現在行を削除します。"),
        new ShortcutEntry("ファイル・エディタ", "Ctrl+Space",
            "単語補完の候補を表示します。"),
        new ShortcutEntry("ファイル・エディタ", "Ctrl+マウスホイール",
            "エディタ本文・差分表示の文字サイズを変えます（8〜32、設定にも記憶されます）。"),

        // ============== 検索 ==============
        new ShortcutEntry("検索", "Ctrl+F",
            "開いているファイル内を検索します。"),
        new ShortcutEntry("検索", "Ctrl+H",
            "開いているファイル内を置換します。"),
        new ShortcutEntry("検索", "Ctrl+Shift+F",
            "プロジェクト全体を横断検索します。", "SelectSearch"),

        // ============== 表示の切り替え ==============
        new ShortcutEntry("表示の切り替え", "Ctrl+Shift+E",
            "サイドビューをエクスプローラ（ファイル一覧）へ切り替えます。", "SelectExplorer"),
        new ShortcutEntry("表示の切り替え", "Ctrl+Shift+H",
            "履歴ビューを開きます。", "ShowHistory"),
        new ShortcutEntry("表示の切り替え", "Ctrl+,",
            "設定を開きます。", "OpenSettings"),
        new ShortcutEntry("表示の切り替え", "F6",
            "サイドビュー→エディタ→ブロック一覧→適用ボタンの順にフォーカスを巡回します。"),
        new ShortcutEntry("表示の切り替え", "Ctrl+/",
            "（ファイル編集中を除く）このショートカット一覧を開きます。", "OpenShortcuts"),
        // 機能改善: コマンドパレット追加。
        new ShortcutEntry("表示の切り替え", "Ctrl+Shift+P",
            "全操作を検索して実行できるコマンドパレットを開きます。", "OpenCommandPalette"),
    };

    /// <summary>
    /// <paramref name="commandKey"/>に対応するキー表記を返す。複数のショートカットが同じ
    /// コマンドに割り当たっている場合は最初に見つかったものを返す（現状はいずれも1対1）。
    /// 見つからない場合（ショートカットの割り当てが無い操作）はnull。
    /// </summary>
    public static string? GestureFor(string commandKey)
        => Entries.FirstOrDefault(e => e.CommandKey == commandKey)?.Gesture;
}
