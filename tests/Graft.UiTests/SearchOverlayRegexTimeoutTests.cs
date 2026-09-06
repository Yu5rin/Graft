using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Platform;
using Graft.ViewModels;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// エディタ内検索（Ctrl+F）で破滅的バックトラックを起こす正規表現を入力するとアプリが
/// プロセスごと落ちていた不具合の回帰テスト。
///
/// 落ちていた経路（実測で確認済み）:
/// <list type="number">
/// <item><see cref="SearchPatternBuilder"/>は暴走防止のため2秒のタイムアウト付きで
/// <see cref="Regex"/>を作る。そのため<c>(a+)+$</c>のような入れ子の量指定子を打ち込むと、
/// 照合時に<see cref="RegexMatchTimeoutException"/>が飛ぶ。</item>
/// <item>この例外を<see cref="SearchOverlayViewModel"/>が誰も捕まえていなかったため、
/// デバウンスのTickハンドラやキー入力ハンドラを素通りして
/// <c>Dispatcher.UIThread.UnhandledException</c>へ届く。</item>
/// <item><c>App.OnDispatcherUnhandledException</c>は<see cref="AvaloniaEditExceptionGuard"/>が
/// trueを返したときしか<c>Handled</c>にしない。この例外の<see cref="Exception.Source"/>は
/// AvaloniaEditではないためfalseとなり、<c>AppDomain.UnhandledException</c>へ抜けて
/// <b>プロセスが終了し、未保存の編集内容がすべて失われていた</b>。</item>
/// </list>
///
/// デバウンス（150ms）経由でも発火するため、検索語を打っている途中の中間状態
/// （<c>(a+)+</c>まで打った時点など）でも起こりうるのが特に厄介な点だった。
/// </summary>
public class SearchOverlayRegexTimeoutTests
{
    /// <summary>破滅的バックトラックを確実に起こすパターンと入力の組。
    /// <c>(a+)+$</c>は<c>a</c>の並びの分割の仕方が指数通りあり、末尾が<c>$</c>に達しない
    /// （最後の1文字が<c>a</c>ではない）ため全分割を試して必ずタイムアウトする。</summary>
    private const string CatastrophicPattern = "(a+)+$";

    private static string CatastrophicText => new string('a', 60) + "!";

    /// <summary>デバウンスのTickを手で発火できるようにしたUI機能一式。
    /// 実機ではこのコールバックを<c>DispatcherTimer.Tick</c>が呼ぶため、
    /// 「コールバックが例外を投げないこと」＝「Tick経由でアプリが落ちないこと」になる。</summary>
    private sealed class ManualTimerUiServices : IUiServices
    {
        private readonly AvaloniaUiServices _inner = new();

        public IClipboardAccess Clipboard => _inner.Clipboard;
        public IScreenInfo Screens => _inner.Screens;

        /// <summary>直近に作られたタイマー。テストから<see cref="ManualTimer.Tick"/>を呼ぶ。</summary>
        public ManualTimer? LastTimer { get; private set; }

        public IUiTimer CreateTimer(TimeSpan interval, Action onTick)
        {
            var timer = new ManualTimer(onTick);
            LastTimer = timer;
            return timer;
        }

        internal sealed class ManualTimer : IUiTimer
        {
            private readonly Action _onTick;

            public ManualTimer(Action onTick) => _onTick = onTick;

            /// <summary><see cref="Restart"/>されて以降、まだ発火していない状態かどうか。</summary>
            public bool IsPending { get; private set; }

            public void Restart() => IsPending = true;
            public void Stop() => IsPending = false;
            public void Dispose() { }

            /// <summary>実機の<c>DispatcherTimer.Tick</c>に相当する発火。</summary>
            public void Tick() => _onTick();
        }
    }

    private sealed class FakeEditor : ITextEditorAccess
    {
        private string _text;

        public FakeEditor(string text) => _text = text;

        public string Text => _text;
        public int CaretOffset { get; private set; }
        public void Select(int offset, int length) => CaretOffset = offset;
        public void ScrollToOffset(int offset) { }

        public void Replace(int offset, int length, string replacement)
            => _text = _text[..offset] + replacement + _text[(offset + length)..];

        public void BeginUndoGroup() { }
        public void EndUndoGroup() { }
    }

    private static SearchOverlayViewModel CreateViewModel(out ManualTimerUiServices ui, string text)
    {
        ui = new ManualTimerUiServices();
        var vm = new SearchOverlayViewModel(ui);
        vm.Attach(new FakeEditor(text));
        return vm;
    }

    [AvaloniaFact(DisplayName = "対照: RegexMatchTimeoutExceptionはAvaloniaEdit由来ではないため、漏れればアプリは必ず落ちる")]
    public void 対照_タイムアウト例外はDispatcherの保護を通り抜ける()
    {
        // 「捕まえ損ねたら本当に落ちるのか」を根拠として固定する。App.OnDispatcherUnhandledExceptionは
        // AvaloniaEditExceptionGuard.ShouldContinueがtrueのときしかHandledにしないため、
        // この例外が呼び出し元まで漏れた時点でプロセス終了が確定する。
        // ＝ ViewModel側で捕まえる以外にアプリを守る手段が無い、ということ。
        var timeout = new RegexMatchTimeoutException();
        AvaloniaEditExceptionGuard.ShouldContinue(timeout).Should().BeFalse(
            "Sourceが\"AvaloniaEdit\"ではないため、App側の保護網では握られずAppDomain.UnhandledExceptionへ抜ける");
    }

    [AvaloniaFact(DisplayName = "対照: 修正前に例外が漏れていた経路（Regex.Matchesの列挙）で実際にタイムアウトが飛ぶ")]
    public void 対照_同じパターンと入力で照合そのものはタイムアウトする()
    {
        // 修正が「例外を捕まえている」のか「そもそも例外が起きない条件になってしまった」のかを
        // 区別できるようにするための対照。SearchPatternBuilderが作るのと同じRegexで、
        // 生の列挙は今も必ずRegexMatchTimeoutExceptionを投げることを固定する。
        var (regex, error) = SearchPatternBuilder.TryBuild(CatastrophicPattern, useRegex: true, caseSensitive: false, wholeWord: false);
        error.Should().BeNull("パターン自体は文法として正しく、コンパイルは成功する（壊れるのは照合時）");
        regex.Should().NotBeNull();

        var act = () =>
        {
            foreach (Match _ in regex!.Matches(CatastrophicText)) { }
        };
        act.Should().Throw<RegexMatchTimeoutException>(
            "この入力とパターンの組み合わせでは破滅的バックトラックが起き、2秒の上限で必ず打ち切られる");
    }

    [AvaloniaFact(DisplayName = "Ctrl+Fで破滅的バックトラックのパターンを入れてもアプリは落ちず、エラー表示になる")]
    public void 破滅的バックトラックでも落ちずエラー表示になる()
    {
        var vm = CreateViewModel(out _, CatastrophicText);
        vm.UseRegex = true;

        var act = () => vm.OpenFind(CatastrophicPattern);

        act.Should().NotThrow("例外が呼び出し元へ漏れると、キー入力ハンドラ経由でアプリがプロセスごと落ちる");
        vm.HasError.Should().BeTrue("不正な正規表現を入れたときと同じ「エラー表示」の見え方にする");
        vm.StatusText.Should().Be(SearchPatternBuilder.TimeoutMessage);
        vm.Matches.Should().BeEmpty("途中まで拾ったヒットを残すと件数表示もハイライトも実態と食い違う");
        vm.CurrentIndex.Should().Be(-1);
    }

    [AvaloniaFact(DisplayName = "不正な正規表現のときと同じ見え方（HasError=true・理由がStatusTextに出る）になる")]
    public void 不正な正規表現のときと同じ見え方になる()
    {
        var timedOut = CreateViewModel(out _, CatastrophicText);
        timedOut.UseRegex = true;
        timedOut.OpenFind(CatastrophicPattern);

        var invalid = CreateViewModel(out _, CatastrophicText);
        invalid.UseRegex = true;
        invalid.OpenFind("(a"); // 閉じ括弧が無く、コンパイル時点で失敗する

        invalid.HasError.Should().BeTrue();
        timedOut.HasError.Should().Be(invalid.HasError, "タイムアウトも不正パターンも同じ「エラー」として扱う");
        timedOut.Matches.Count.Should().Be(invalid.Matches.Count);
        timedOut.StatusText.Should().NotBeNullOrEmpty("理由が分かる文言が出ていること");
        invalid.StatusText.Should().NotBeNullOrEmpty();
    }

    [AvaloniaFact(DisplayName = "デバウンス経由（検索語の入力途中）でも落ちない")]
    public void デバウンスのTick経由でも落ちない()
    {
        var vm = CreateViewModel(out var ui, CatastrophicText);
        vm.UseRegex = true;
        vm.OpenFind("a"); // まず普通のパターンで開く（ここまでは正常に一致する）
        vm.HasError.Should().BeFalse();

        // 利用者が続けて "(a+)+$" まで打った状態を作る。Queryのsetterはデバウンスを
        // 予約するだけで、実際の走査はタイマーのTickで走る（＝実機ではDispatcherTimer.Tick）。
        vm.Query = CatastrophicPattern;
        ui.LastTimer.Should().NotBeNull();
        ui.LastTimer!.IsPending.Should().BeTrue("入力のたびにデバウンスが予約されること");

        var act = () => ui.LastTimer!.Tick();

        act.Should().NotThrow("Tickから漏れた例外はDispatcher.UIThread.UnhandledExceptionへ抜けてプロセスを落とす");
        vm.HasError.Should().BeTrue();
        vm.StatusText.Should().Be(SearchPatternBuilder.TimeoutMessage);
    }

    [AvaloniaFact(DisplayName = "「すべて置換」の再走査でタイムアウトしても落ちない")]
    public void すべて置換の再走査でも落ちない()
    {
        // 置換前は普通に一致するが、置換の結果できあがった本文に対する再走査で
        // 破滅的バックトラックが起きる状況を作る。ReplaceAllは末尾でRecomputeNowを呼ぶため、
        // そこが保護されていないと置換の直後にアプリが落ちる。
        var editor = new FakeEditor("X" + new string('a', 60) + "!");
        var ui = new ManualTimerUiServices();
        var vm = new SearchOverlayViewModel(ui);
        vm.Attach(editor);
        vm.UseRegex = true;
        vm.OpenReplace("X"); // まず「X」を1件見つけておく
        vm.Matches.Should().HaveCount(1);

        vm.ReplaceText = string.Empty;
        vm.Query = CatastrophicPattern; // 再走査で使われるパターンを破滅的なものへ差し替える
        ui.LastTimer!.Stop();           // デバウンスは発火させず、_matchesは「X」のまま維持する

        var act = () => vm.ReplaceAllCommand.Execute(null);

        act.Should().NotThrow("置換後の再走査で漏れた例外もアプリをプロセスごと落とす");
        vm.HasError.Should().BeTrue();
        vm.StatusText.Should().Be(SearchPatternBuilder.TimeoutMessage);
    }

    [AvaloniaFact(DisplayName = "デグレ防止: 正常な正規表現検索はこれまでどおり動く")]
    public void 正常な正規表現検索はこれまでどおり動く()
    {
        var vm = CreateViewModel(out _, "foo1 bar foo22 baz foo333");
        vm.UseRegex = true;
        vm.OpenFind(@"foo\d+");

        vm.HasError.Should().BeFalse();
        vm.Matches.Should().HaveCount(3);
        vm.Matches.Select(m => m.Value).Should().Equal("foo1", "foo22", "foo333");
        vm.StatusText.Should().Be("1 / 3");

        vm.CommitAndFindNext();
        vm.CurrentIndex.Should().Be(1);
        vm.StatusText.Should().Be("2 / 3");

        vm.CommitAndFindPrevious();
        vm.CurrentIndex.Should().Be(0);
        vm.StatusText.Should().Be("1 / 3");
    }

    [AvaloniaFact(DisplayName = "デグレ防止: 正常な正規表現での「すべて置換」（後方参照つき）もこれまでどおり動く")]
    public void 正常な正規表現でのすべて置換はこれまでどおり動く()
    {
        var editor = new FakeEditor("foo1 bar foo22");
        var vm = new SearchOverlayViewModel(new ManualTimerUiServices());
        vm.Attach(editor);
        vm.UseRegex = true;
        vm.OpenReplace(@"foo(\d+)");
        vm.Matches.Should().HaveCount(2);

        vm.ReplaceText = "[$1]";
        vm.ReplaceAllCommand.Execute(null);

        editor.Text.Should().Be("[1] bar [22]");
        vm.HasError.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "デグレ防止: 正規表現をOFFにした通常検索は特殊文字をそのまま探す")]
    public void 正規表現をOFFにした通常検索はこれまでどおり動く()
    {
        var vm = CreateViewModel(out _, "a+b と (a+)+$ の2箇所");
        vm.UseRegex = false;
        vm.OpenFind("(a+)+$"); // 正規表現としては破滅的だが、OFFなのでただの文字列として探す

        vm.HasError.Should().BeFalse("正規表現OFFではエスケープされるため破滅的バックトラックは起きない");
        vm.Matches.Should().HaveCount(1);
        vm.StatusText.Should().Be("1 / 1");
    }
}
