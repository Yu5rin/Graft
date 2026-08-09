using FluentAssertions;
using Graft.Core;
using Graft.Platform;
using Graft.Views;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 10件目の不具合修正の回帰テスト: グローバルホットキーの登録は起動時の1回だけで、設定画面での
/// 変更が再起動まで反映されなかった不具合。
///
/// 実際のOS資源（<see cref="Graft.Platform.Linux.LinuxGlobalHotkeys"/>・
/// <see cref="Graft.Platform.Windows.WindowsGlobalHotkeys"/>）に触れる経路は、既存方針
/// （ClipboardWatchTests.csのコメント参照）どおり単体テストでは呼ばず実機（Xvfb）検証に委ねる。
/// ここでは<see cref="IGlobalHotkeys"/>のフェイク実装を使い、
/// <see cref="StartupCoordinator.ReapplyHotkey"/>（登録・失敗時のロールバック・警告文の組み立て）
/// というロジック部分だけを、失敗を自在に注入しながら検証する。
/// </summary>
public class HotkeyReapplyTests
{
    private const string PromptCopyHotkey = "Ctrl+Shift+C";

    [Fact(DisplayName = "新しい組み合わせの登録に成功すると、ActiveGestureが新しい値になり警告は出ない")]
    public void 新しい組み合わせへの再登録が成功する()
    {
        var hotkeys = new FakeGlobalHotkeys();

        var outcome = StartupCoordinator.ReapplyHotkey(
            hotkeys, previousGesture: "Ctrl+Alt+V", newGesture: "Ctrl+Alt+B", PromptCopyHotkey,
            pasteCallback: () => { }, copyCallback: () => { });

        outcome.ActiveGesture.Should().Be("Ctrl+Alt+B", "新しい組み合わせで登録できたはず");
        outcome.WarningMessage.Should().BeNull("成功時は警告を出してはならない");
        hotkeys.RegisteredGestures.Should().BeEquivalentTo(new[] { "Ctrl+Alt+B", PromptCopyHotkey },
            "貼り付け・プロンプトコピーの両方が新しい状態で実際に登録されているはず");
    }

    [Fact(DisplayName = "新しい組み合わせが他アプリに使われていて失敗すると、古い組み合わせへ登録し戻したうえで警告が出る")]
    public void 失敗時に古い組み合わせへロールバックし警告が出る()
    {
        // "Ctrl+Alt+B" を他アプリが既に掴んでいる状況を模す。
        var hotkeys = new FakeGlobalHotkeys(failGestures: "Ctrl+Alt+B");

        var outcome = StartupCoordinator.ReapplyHotkey(
            hotkeys, previousGesture: "Ctrl+Alt+V", newGesture: "Ctrl+Alt+B", PromptCopyHotkey,
            pasteCallback: () => { }, copyCallback: () => { });

        outcome.ActiveGesture.Should().Be("Ctrl+Alt+V", "登録に失敗した以上、古い組み合わせへ戻っているはず（新旧どちらも効かない状態を作らない）");
        outcome.WarningMessage.Should().NotBeNull("失敗を握り潰さず警告を出す必要がある");
        outcome.WarningMessage.Should().Contain("Ctrl+Alt+B").And.Contain("Ctrl+Alt+V");

        // 最終的にOS側へ実際に登録されている内容も、古い組み合わせ＋プロンプトコピーのはず。
        hotkeys.RegisteredGestures.Should().BeEquivalentTo(new[] { "Ctrl+Alt+V", PromptCopyHotkey });
    }

    [Fact(DisplayName = "起動時から一度も成功していない場合（戻す先が無い）に失敗しても、例外にならず警告だけが出る")]
    public void 戻す先が無い場合は警告のみを出す()
    {
        var hotkeys = new FakeGlobalHotkeys(failGestures: "Ctrl+Alt+B");

        var outcome = StartupCoordinator.ReapplyHotkey(
            hotkeys, previousGesture: null, newGesture: "Ctrl+Alt+B", PromptCopyHotkey,
            pasteCallback: () => { }, copyCallback: () => { });

        outcome.ActiveGesture.Should().BeNull("元々何も登録できていなかったので、戻す先が無い");
        outcome.WarningMessage.Should().NotBeNull();
        outcome.WarningMessage.Should().Contain("Ctrl+Alt+B");
        hotkeys.RegisteredGestures.Should().BeEmpty("新規登録に失敗し、戻す先も無いので何も登録されていない");
    }

    [Fact(DisplayName = "新しい組み合わせだけでなく古い組み合わせへの登録し直しまで失敗すると、ActiveGestureはnullになり両方無効である旨の警告が出る")]
    public void ロールバックにも失敗すると両方無効になったことを警告する()
    {
        // 極端なケース: 新しい組み合わせだけでなく、戻そうとした古い組み合わせも
        // （例えば別アプリが同時に奪っていた等の理由で）失敗する。
        var hotkeys = new FakeGlobalHotkeys(failGestures: new[] { "Ctrl+Alt+B", "Ctrl+Alt+V" });

        var outcome = StartupCoordinator.ReapplyHotkey(
            hotkeys, previousGesture: "Ctrl+Alt+V", newGesture: "Ctrl+Alt+B", PromptCopyHotkey,
            pasteCallback: () => { }, copyCallback: () => { });

        outcome.ActiveGesture.Should().BeNull("戻す再登録にも失敗したので、実際にはどちらも効いていない");
        outcome.WarningMessage.Should().NotBeNull();
        outcome.WarningMessage.Should().Contain("いずれも無効");
        hotkeys.RegisteredGestures.Should().BeEmpty();
    }

    [Fact(DisplayName = "プロンプトコピー側（固定の組み合わせ）だけが失敗しても、貼り付け側だけ新しい組み合わせのまま片肺で残したりはしない")]
    public void プロンプトコピー側だけの失敗も失敗として扱われる()
    {
        // プロンプトコピー側の組み合わせ自体が（貼り付け側のジェスチャーに関わらず）常に失敗する
        // 状況を模す。この場合、ロールバック（古い貼り付けの組み合わせでの再登録）を試みても
        // プロンプトコピー側は同じ理由で失敗し続けるため、最終的にはどちらも無効になる。
        var hotkeys = new FakeGlobalHotkeys(failGestures: PromptCopyHotkey);

        var outcome = StartupCoordinator.ReapplyHotkey(
            hotkeys, previousGesture: "Ctrl+Alt+V", newGesture: "Ctrl+Alt+B", PromptCopyHotkey,
            pasteCallback: () => { }, copyCallback: () => { });

        outcome.ActiveGesture.Should().BeNull(
            "プロンプトコピー側が常に失敗する以上、貼り付け側だけを新しい・古いいずれの組み合わせであっても片肺で有効なままにはしない");
        outcome.WarningMessage.Should().NotBeNull("失敗を握り潰さず警告を出す必要がある");
        hotkeys.RegisteredGestures.Should().BeEmpty(
            "貼り付け側だけが登録されたまま残る、というような中途半端な状態を残してはならない");
    }

    [Fact(DisplayName = "変更の都度、必ずUnregisterAllしてから登録し直す（IDでの個別解除手段が無いIGlobalHotkeysの制約）")]
    public void 毎回UnregisterAllしてから登録し直す()
    {
        var hotkeys = new FakeGlobalHotkeys();

        StartupCoordinator.ReapplyHotkey(
            hotkeys, previousGesture: "Ctrl+Alt+V", newGesture: "Ctrl+Alt+B", PromptCopyHotkey,
            pasteCallback: () => { }, copyCallback: () => { });

        hotkeys.UnregisterAllCallCount.Should().Be(1, "成功時は最初の解除1回のみで済むはず（ロールバックの追加解除は不要）");
    }

    /// <summary>実際のOS資源に一切触れないフェイク。指定した組み合わせのRegisterだけ失敗させる。</summary>
    private sealed class FakeGlobalHotkeys : IGlobalHotkeys
    {
        private readonly HashSet<string> _failGestures;

        public FakeGlobalHotkeys(params string[] failGestures) => _failGestures = new HashSet<string>(failGestures);

        public List<string> RegisteredGestures { get; } = new();

        public int UnregisterAllCallCount { get; private set; }

        public bool IsSupported => true;

        public string? UnsupportedReason => null;

        public void Attach(IntPtr hwnd)
        {
            // フェイクではウィンドウハンドルを使わない。
        }

        public GraftResult<int> Register(string gesture, Action callback)
        {
            if (_failGestures.Contains(gesture))
            {
                return GraftResult<int>.Fail(GraftIssue.Of(ErrorCode.E601,
                    $"'{gesture}' はテストにより登録失敗を注入しています。"));
            }

            RegisteredGestures.Add(gesture);
            return GraftResult<int>.Ok(RegisteredGestures.Count);
        }

        public void UnregisterAll()
        {
            UnregisterAllCallCount++;
            RegisteredGestures.Clear();
        }

        public bool HandleMessage(int msg, IntPtr wParam, IntPtr lParam) => false;

        public void Dispose()
        {
            // フェイクでは何も解放しない。
        }
    }
}
