using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Graft.UiTests.TestSupport;

/// <summary>
/// テスト内で <c>Show()</c> したウィンドウを覚えておき、テストの後始末（<c>Dispose()</c>）で
/// まとめて <c>Close()</c> するヘルパ。
///
/// なぜこれが要るか（CIで不定期に起きる「Unable to locate 'Avalonia.Platform.IFontManagerImpl'」の原因）:
/// Avalonia.Headless 11.2.3 の <c>HeadlessUnitTestSession.EnsureApplication()</c> が返す
/// 後始末処理は、逆コンパイルで確認したところ次の順序になっている。
/// <list type="number">
/// <item><c>scope.Dispose()</c> — <see cref="Avalonia.AvaloniaLocator"/>のスコープを破棄する
/// （<c>Avalonia.Platform.IFontManagerImpl</c>の登録はここで消える）。</item>
/// <item><c>Dispatcher.ResetForUnitTests()</c> — 保留中のディスパッチャジョブ（レイアウト・
/// 描画）をここで初めて流す。</item>
/// </list>
/// つまり「フォント基盤が消えたあとにレイアウト・描画が走る」順序になっている。テストが
/// ウィンドウを表示（<c>Show()</c>）したまま <c>Close()</c> も <c>Dispatcher.UIThread.RunJobs()</c>
/// もせずに終わると、視覚ツリーに保留中のレイアウトが残った状態でこの最終処理を迎え、
/// <c>AvaloniaEdit.Rendering.TextView.MeasureOverride</c>（<see cref="Avalonia.Media.FontManager.Current"/>
/// を呼ぶ）がフォント基盤の無い状態で走って例外になる。この処理は次にディスパッチャの
/// キューが流れたタイミングで走るため、例外が実際の原因テストではなく別のテストの失敗として
/// 報告される（CIでの「毎回違うテスト名が失敗する」症状はこれで説明が付く）。
///
/// <see cref="Graft.Views.ShellWindow"/>はAvaloniaEditの<c>TextView</c>を内包するため、
/// このヘルパで閉じ忘れを防ぐ主な対象になる。<c>SettingsWindow</c>等それ以外のウィンドウも
/// 同じ理由（表示されたまま終わるとレイアウトが保留になりうる）でここに乗せてよい。
/// </summary>
public sealed class ShownWindowTracker : IDisposable
{
    private readonly List<Window> _windows = new();

    /// <summary>後始末に失敗した回数（複数テストにまたがる累計）。テストの成否には使わない、
    /// 調査用の補助カウンタ（要件3: 「ウィンドウが残っていないこと」の確認手段の検討）。
    /// これ自体をテストの合否に使うと、後始末の失敗（本来テスト結果と無関係）でテストが
    /// 不安定になりかねないため、あえてどこでもアサートしない。</summary>
    public static int CloseFailureCount => _closeFailureCount;

    private static int _closeFailureCount;

    /// <summary>表示したウィンドウを後始末対象として登録する。<c>_windows.Track(new ShellWindow(shell))</c>
    /// のように、生成した場でそのまま包めるよう、渡したウィンドウ自身を返す。</summary>
    public TWindow Track<TWindow>(TWindow window) where TWindow : Window
    {
        _windows.Add(window);
        return window;
    }

    /// <summary>
    /// 登録済みのウィンドウを、後から開いたものから順に（逆順で）閉じる。
    /// 親ウィンドウを先に閉じると子ウィンドウ（オーナー付きダイアログ等）の後始末が
    /// 不安定になりうるため、開いた順序と逆に辿るのが安全側。
    ///
    /// 1つのウィンドウの <c>Close()</c> が例外を投げても、それ以降のウィンドウの後始末は
    /// 止めない（後始末の失敗が芋づる式に他の後始末を妨げ、フォント基盤の解放後に
    /// レイアウトが残る事態を悪化させないため）。
    /// </summary>
    public void Dispose()
    {
        if (_windows.Count == 0)
        {
            // 一度もTrack()されなかった場合（ウィンドウを表示しないテストメソッド。同じ
            // テストクラス内に[AvaloniaFact]と素の[Fact]が混在するケースを含む）は何もしない。
            // Dispatcher.UIThread.RunJobs()はAvaloniaのUIスレッド以外から呼ぶと
            // 「Call from invalid thread」で例外になるため、無用な呼び出しを避けて早期returnする。
            return;
        }

        for (var i = _windows.Count - 1; i >= 0; i--)
        {
            try
            {
                _windows[i].Close();
            }
            catch
            {
                // ここでの失敗はテストの成否と無関係（後始末のベストエフォート）。
                // ただし完全に揉み消すと調査しづらいため、件数だけは記録しておく。
                Interlocked.Increment(ref _closeFailureCount);
            }
        }

        _windows.Clear();

        // 全て閉じたことで生じた保留中のディスパッチャジョブ（レイアウト・描画）を、
        // ロケータスコープがまだ生きているこの時点で出し切っておく。これをしないと、
        // Avalonia.Headless側の最終ResetForUnitTests()（ロケータスコープ破棄の後）まで
        // 保留分が持ち越され、フォント基盤が無い状態でレイアウトが走ってしまう
        // （クラス冒頭のコメント参照）。
        Dispatcher.UIThread.RunJobs();
    }
}
