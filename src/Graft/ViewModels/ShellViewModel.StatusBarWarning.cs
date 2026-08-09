using System.ComponentModel;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="ShellViewModel"/> の分割ファイル（1ファイル400行上限のため）。
///
/// ステータスバーの警告表示（課題1の書き込み不可・8.6のシンタックスハイライト無効化・
/// 課題3の極端に長い行の無効化・10件目の不具合修正のグローバルホットキー再登録失敗）を
/// 1箇所へ集約する。
///
/// 経緯: これらはもともとステータスバーの同じ行へ、警告ごとに独立したTextBlockとして
/// 並べて実装されていた（担当が別々だったため、実装のタイミングもばらばらだった）。
/// しかし複数の警告が同時に成立する組み合わせ（例: 書き込み不可のフォルダで、極端に
/// 長い行を含むファイルを開く）を実機のXvfb環境で検証したところ、ウィンドウの最小幅
/// （960px、<c>Themes/Tokens.axaml</c> の <c>MinWindowWidth</c>）では警告文どうし、
/// および右側の接ぎ木状態表示（<see cref="MainViewModel.StatusSummaryText"/>）と文字が
/// 重なって判読できなくなることを実機のスクリーンショットで確認した。
///
/// 対処: 表示スロットを1つに絞り、複数成立している場合は最も深刻な警告のみを表示して
/// 「ほかN件」を付け足す（<see cref="MainViewModel.TargetSummaryText"/>の「ほかN件」表記と
/// 同じ考え方）。隠れた警告を含む全文は<see cref="StatusBarWarningTooltip"/>（ToolTip）で
/// 確認できるため、情報自体は失われない。優先順位は「アプリ全体・データの喪失に関わるもの」
/// を最優先とし、以降は「アクティブな1タブの表示上の制約に留まるもの」を並べた
/// （<see cref="_statusBarWarningSources"/>の定義順＝優先順位）。
/// </summary>
public sealed partial class ShellViewModel
{
    // 10件目の不具合修正: グローバルホットキーの再登録に失敗した際の警告文（成功中・未変更中は
    // null）。StartupCoordinator.ReapplyHotkeyIfChanged（StartupCoordinator.Hotkey.cs）が
    // 設定変更のたびに呼び直す。他の警告源（Diff/Editorのプロパティ）と違い、この情報を持つ
    // 別のViewModelが存在しない（StartupCoordinatorが直接の当事者）ため、中継用の
    // PropertyChangedハンドラを介さず<see cref="SetHotkeyRegistrationWarning"/>から
    // 直接NotifyStatusBarWarningChangedを呼ぶ。
    private string? _hotkeyRegistrationWarning;

    /// <summary>
    /// ステータスバーに出しうる警告の定義。判定関数と、短い表示用テキスト・詳細
    /// （ToolTip用、書き込み不可のみ対処方法を含む長文）の組。上から優先順位順。
    /// ホットキー再登録失敗はデータ喪失こそ伴わないがアプリ全体（1タブに留まらない）の機能低下
    /// のため、書き込み不可の次・タブ固有の警告2件より前に置く。
    /// </summary>
    private (Func<bool> IsActive, string Text, string Detail)[] BuildStatusBarWarningSources() => new (Func<bool>, string, string)[]
    {
        (
            () => Graft.IsDataDirectoryReadOnly,
            "書き込み不可のため設定・履歴・バックアップは保存されません",
            "実行ファイルと同じフォルダへ書き込む権限がありません。書き込み権限のあるフォルダへGraftのフォルダ一式を移動してから起動し直してください。"
        ),
        (
            () => _hotkeyRegistrationWarning is not null,
            "グローバルホットキーの登録に失敗しました",
            _hotkeyRegistrationWarning ?? string.Empty
        ),
        (
            () => Graft.Diff.SyntaxHighlightDisabled,
            "シンタックスハイライトを無効化しました",
            "シンタックスハイライトを無効化しました"
        ),
        (
            () => Editor.ActiveTabHasLongLineWarning,
            "極端に長い行があるため構文強調・折り返しを無効化しました",
            "極端に長い行があるため構文強調・折り返しを無効化しました"
        ),
    };

    /// <summary>ステータスバーに警告を1件でも表示すべきかどうか。</summary>
    public bool HasStatusBarWarning => BuildStatusBarWarningSources().Any(s => s.IsActive());

    /// <summary>
    /// ステータスバーに表示する警告文。複数成立している場合は最優先の1件＋「ほかN件」。
    /// 何も無ければ空文字列を返す（<see cref="HasStatusBarWarning"/>で表示可否を分けるため、
    /// 呼び出し側で空文字列かどうかを別途見る必要はない）。
    /// </summary>
    public string StatusBarWarningText
    {
        get
        {
            var active = BuildStatusBarWarningSources().Where(s => s.IsActive()).ToList();
            if (active.Count == 0) return string.Empty;
            return active.Count == 1 ? active[0].Text : $"{active[0].Text}　ほか{active.Count - 1}件";
        }
    }

    /// <summary>
    /// ToolTip用の全文。1件のみならその詳細、複数ならすべての詳細を改行区切りで並べる
    /// （表示上は省略された警告があっても、ここで必ず全文を確認できるようにするため）。
    /// </summary>
    public string StatusBarWarningTooltip
    {
        get
        {
            var active = BuildStatusBarWarningSources().Where(s => s.IsActive()).ToList();
            return string.Join(Environment.NewLine, active.Select(s => s.Detail));
        }
    }

    /// <summary>
    /// 警告の判定材料（Graft.IsDataDirectoryReadOnly・Graft.Diff.SyntaxHighlightDisabled・
    /// Editor.ActiveTabHasLongLineWarning）はいずれも別々のViewModelのプロパティであるため、
    /// それぞれの変更をここへ中継し、<see cref="StatusBarWarningText"/>等のPropertyChangedを
    /// まとめて発火させる。Graft自体のPropertyChangedは既存の<c>OnGraftPropertyChanged</c>
    /// （ShellViewModel.cs）が購読済みのため、そちらからも呼べるよう
    /// <see cref="NotifyStatusBarWarningChanged"/>をpublicではなくprivateのまま同ファイル内で共有する。
    /// </summary>
    private void WireStatusBarWarningSources()
    {
        Graft.Diff.PropertyChanged += OnDiffPropertyChangedForStatusBarWarning;
        Editor.PropertyChanged += OnEditorPropertyChangedForStatusBarWarning;
    }

    private void OnDiffPropertyChangedForStatusBarWarning(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiffViewModel.SyntaxHighlightDisabled)) NotifyStatusBarWarningChanged();
    }

    private void OnEditorPropertyChangedForStatusBarWarning(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorPaneViewModel.ActiveTabHasLongLineWarning)) NotifyStatusBarWarningChanged();
    }

    private void NotifyStatusBarWarningChanged()
    {
        OnPropertyChanged(nameof(HasStatusBarWarning));
        OnPropertyChanged(nameof(StatusBarWarningText));
        OnPropertyChanged(nameof(StatusBarWarningTooltip));
    }

    /// <summary>
    /// 10件目の不具合修正: StartupCoordinatorがホットキーの再登録を試みるたびに呼ぶ。成功時・
    /// 何も変化がなければnullを渡す（警告を消す）。失敗時は理由を含む日本語文言を渡す
    /// （StartupCoordinator.Hotkey.cs の ReapplyHotkey 参照）。
    /// </summary>
    public void SetHotkeyRegistrationWarning(string? message)
    {
        if (_hotkeyRegistrationWarning == message) return;
        _hotkeyRegistrationWarning = message;
        NotifyStatusBarWarningChanged();
    }
}
