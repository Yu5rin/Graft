using System.Linq;
using Graft.Core;
using Graft.Platform;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// <see cref="StartupCoordinator"/> の分割ファイル（1ファイル400行上限のため）。
///
/// 10件目の不具合修正: グローバルホットキー（8.10章）の登録は起動時
/// （<see cref="StartAsync"/>→<see cref="WirePlatformServices"/>）の1回だけで、設定画面で
/// ホットキーの組み合わせ（<see cref="Infra.Settings.Hotkey"/>）を変更しても再登録されず、
/// 次回起動まで反映されなかった。9件目のクリップボード監視修正
/// （<see cref="ApplyLiveSettingsChange"/>・<see cref="ToggleClipboardWatch"/>）と同じ
/// 「設定の前後比較→変化があれば実際の資源へ反映」の流儀に揃える。
///
/// クリップボード監視との違い: クリップボード監視の開始・停止はほぼ失敗しない操作
/// （対応環境かどうかは設定画面のチェックボックス自体を無効化して事前に弾いている。
/// SettingsViewModel.IsClipboardWatchSupported参照）。一方グローバルホットキーはOS全体から
/// 入力を奪う機能で、他アプリが既に同じ組み合わせを使っていると
/// <see cref="IGlobalHotkeys.Register"/>が実機で普通に失敗しうる（要件: 握り潰さない）。
/// そのため、クリップボード監視の切り替えには無かった以下2点をここで追加する。
///   - 失敗したら新旧どちらも効かない状態を作らない（古い組み合わせへ戻す）
///   - 失敗を握り潰さず、ステータスバーの警告スロット（<see cref="ShellViewModel.
///     StatusBarWarning.cs"/>・<see cref="ShellViewModel.SetHotkeyRegistrationWarning"/>）で
///     利用者に知らせる
///
/// _isApplyInProgressとの関係: ホットキーの登録・解除はApplyEngine・MatchEngineのいずれも
/// 参照せず、書き込み中のファイルへ一切影響しない。クリップボード監視の切り替え
/// （<see cref="ToggleClipboardWatch"/>のコメント参照）と全く同じ理由で、適用処理が
/// 実行中かどうかに関わらずその場で切り替えてよい（MainViewModel.UpdateSettingsが行う
/// 「適用処理中は反映を保留する」対象には含めない）。
///
/// テスト容易性: 実際のOS資源（<see cref="IGlobalHotkeys"/>の具象実装）に触れる経路は既存方針
/// （ClipboardWatchTests.csのコメント参照: トレイ・ホットキー・クリップボード監視などOS資源に
/// 直接触れるStartAsync自体は単体テストで呼ばず実機（Xvfb）検証に委ねる）どおりだが、失敗時の
/// ロールバック・警告文の組み立てというロジック自体は<see cref="IGlobalHotkeys"/>のフェイクだけで
/// 検証できるよう、<see cref="ReapplyHotkey"/>をstatic・IGlobalHotkeys引数受け取りの純粋な形に
/// 切り出している（HotkeyReapplyTests.cs参照）。
/// </summary>
public sealed partial class StartupCoordinator
{
    // 現在OS側へ実際に登録できている「貼り付け」ホットキーの組み合わせ。起動時・再登録の
    // いずれかが成功した直後にのみ更新する（つまり「実際に効いている値」を表す）。
    // 登録そのものに一度も成功していない場合はnullのままで、その場合は再登録に失敗しても
    // 戻す先が無い（そもそも起動時から効いていない）ため、ロールバックは行わない。
    private string? _activeHotkeyGesture;

    /// <summary>
    /// 起動時（<see cref="WirePlatformServices"/>）専用: 「貼り付け」「プロンプトをコピー」の
    /// 2つのホットキーを最初に登録する。失敗はissuesへ積んで起動時レポート（StartupReport）へ
    /// 委ねる（既存の挙動をそのまま維持。ここを変えると起動時失敗の扱いが変わってしまうため）。
    /// </summary>
    private void RegisterInitialHotkeys(ShellWindow window, MainViewModel mainViewModel, List<GraftIssue> issues)
    {
        var pasteResult = _platform.Hotkeys.Register(_settings.Hotkey, () => OnPasteHotkey(window, mainViewModel));
        var copyResult = _platform.Hotkeys.Register(PromptCopyHotkey, () => OnCopyPromptHotkey(mainViewModel));
        issues.AddRange(pasteResult.Issues);
        issues.AddRange(copyResult.Issues);

        // プロンプトコピー側の成否に関わらず、貼り付けホットキーが実際に掴み取れていれば
        // 「現在効いている組み合わせ」として記録する（再登録時のロールバック先になる）。
        if (pasteResult.IsSuccess)
        {
            _activeHotkeyGesture = _settings.Hotkey;
        }
    }

    /// <summary>
    /// 設定画面での「貼り付け」ホットキー変更を、実行中のアプリへ再起動なしで反映する。
    /// <see cref="ApplyLiveSettingsChange"/>から、設定画面での保存確定のたびに呼ぶ
    /// （実際に値が変わっていない呼び出しはこの中で無視するため、呼び出し側で事前に
    /// 前後比較する必要はない）。
    /// </summary>
    private void ReapplyHotkeyIfChanged(string newGesture, ShellWindow window, MainViewModel mainViewModel)
    {
        if (newGesture == _activeHotkeyGesture) return;

        var outcome = ReapplyHotkey(
            _platform.Hotkeys,
            _activeHotkeyGesture,
            newGesture,
            PromptCopyHotkey,
            () => OnPasteHotkey(window, mainViewModel),
            () => OnCopyPromptHotkey(mainViewModel));

        _activeHotkeyGesture = outcome.ActiveGesture;
        _shellViewModel?.SetHotkeyRegistrationWarning(outcome.WarningMessage);

        if (outcome.WarningMessage is not null)
        {
            _logger?.Warn("hotkey", outcome.WarningMessage);
        }
    }

    /// <summary>
    /// 実際の再登録処理（ロールバック含む）の本体。<see cref="IGlobalHotkeys"/>にはID指定の
    /// 個別解除が無く<see cref="IGlobalHotkeys.UnregisterAll"/>しか無いため、「貼り付け」
    /// 「プロンプトをコピー」の2つを常にセットで解除・再登録する（後者は組み合わせ固定だが、
    /// 道連れで解除されるため再登録し直さないと消えたままになる）。
    ///
    /// 手順:
    ///  1. 新しい組み合わせで両方登録し直す。両方成功なら完了。
    ///  2. 片方でも失敗したら全解除し、古い組み合わせ（<paramref name="previousGesture"/>）で
    ///     再度両方登録し直す（新旧どちらも効かない状態を避けるため）。
    ///  3. 戻す先（previousGesture）自体が無い、または戻す再登録すら失敗した場合は
    ///     「現在効いている組み合わせ無し」を表すnullを返す。
    ///
    /// staticかつ<see cref="IGlobalHotkeys"/>を引数で受け取る形にしているのは、フェイク実装で
    /// 失敗を注入した単体テスト（HotkeyReapplyTests.cs）から、実際のOS資源（X11/RegisterHotKey）
    /// に一切触れずにロールバック・警告文の組み立てを検証できるようにするため。
    /// </summary>
    public static HotkeyReapplyOutcome ReapplyHotkey(
        IGlobalHotkeys hotkeys, string? previousGesture, string newGesture, string promptCopyGesture,
        Action pasteCallback, Action copyCallback)
    {
        if (TryRegisterPair(hotkeys, newGesture, promptCopyGesture, pasteCallback, copyCallback, out var newFailures))
        {
            return new HotkeyReapplyOutcome(newGesture, null);
        }

        var failureDetailText = BuildFailureDetailText(newFailures);

        if (previousGesture is not null &&
            TryRegisterPair(hotkeys, previousGesture, promptCopyGesture, pasteCallback, copyCallback, out _))
        {
            return new HotkeyReapplyOutcome(previousGesture,
                $"ホットキー '{newGesture}' への変更に失敗したため、以前の組み合わせ '{previousGesture}' のまま維持します。{failureDetailText}");
        }

        // ここへ来るのは (a) 戻す先（previousGesture）自体が無い＝起動時から一度も成功していない、
        // (b) 戻す再登録にすら失敗した（他アプリが古い組み合わせも同時に奪った等の極端なケース）、
        // のいずれか。どちらも「現在グローバルホットキーが一切効かない」状態のため、状態を表す
        // ActiveGestureはnullで統一し、文言だけを状況に応じて出し分ける。
        var message = previousGesture is null
            ? $"ホットキー '{newGesture}' の登録に失敗しました。{failureDetailText}"
            : $"ホットキー '{newGesture}' への変更に失敗し、以前の組み合わせ '{previousGesture}' への登録し直しにも失敗したため、" +
              $"グローバルホットキーは現在いずれも無効です。{failureDetailText}";
        return new HotkeyReapplyOutcome(null, message);
    }

    /// <summary>
    /// 「貼り付け」「プロンプトをコピー」の2つを1組として登録を試みる。片方でも失敗したら、
    /// 中途半端に片方だけ登録できてしまった状態（例: プロンプトコピーだけ新しい状態のまま、
    /// 貼り付けは無効）を残さないよう、その場で全解除してfalseを返す
    /// （<see cref="IGlobalHotkeys"/>にはID指定の個別解除が無く<see cref="IGlobalHotkeys.
    /// UnregisterAll"/>しか無いため、常に「解除→2つとも登録→ダメなら全解除」という
    /// 単位で扱うのが最も単純で状態を追いやすい）。
    /// </summary>
    private static bool TryRegisterPair(
        IGlobalHotkeys hotkeys, string pasteGesture, string promptCopyGesture,
        Action pasteCallback, Action copyCallback, out List<GraftIssue> issues)
    {
        hotkeys.UnregisterAll();
        var pasteResult = hotkeys.Register(pasteGesture, pasteCallback);
        var copyResult = hotkeys.Register(promptCopyGesture, copyCallback);
        issues = pasteResult.Issues.Concat(copyResult.Issues).ToList();

        if (pasteResult.IsSuccess && copyResult.IsSuccess) return true;

        hotkeys.UnregisterAll();
        return false;
    }

    private static string BuildFailureDetailText(IReadOnlyCollection<GraftIssue> issues)
        => issues.Count > 0
            ? Environment.NewLine + string.Join(Environment.NewLine, issues.Select(i => i.ToDisplayText()))
            : string.Empty;
}

/// <summary>
/// <see cref="StartupCoordinator.ReapplyHotkey"/>の結果。<see cref="ActiveGesture"/>は
/// 再登録処理の後で実際にOS側へ登録できている「貼り付け」ホットキーの組み合わせ
/// （何も効いていない場合はnull）。<see cref="WarningMessage"/>は失敗時のみ非null
/// （成功時はステータスバーの警告を消すためnullを返す）。
/// </summary>
public readonly record struct HotkeyReapplyOutcome(string? ActiveGesture, string? WarningMessage);
