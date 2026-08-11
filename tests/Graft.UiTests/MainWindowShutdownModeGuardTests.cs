using System;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 不具合修正: 起動時（データ保存先の復帰確認ダイアログ・起動時の書き込み権限警告ダイアログ等）
/// にメインウィンドウがまだ1枚も無い状態でダイアログを1枚だけ開閉すると、Avalonia側の既定
/// <c>ShutdownMode.OnLastWindowClose</c>が「開いているウィンドウが0枚になった」と判定し、
/// メインウィンドウが立つより前にアプリごと終了してしまっていた（実機のXvfb環境で、復帰確認
/// ダイアログの「はい」を押した直後に後始末ログだけが記録されウィンドウが一切表示されない
/// ことを確認した）。<see cref="Graft.App.OnFrameworkInitializationCompleted"/>は、
/// <c>desktop.MainWindow</c>を割り当てるまでの間だけ<c>ShutdownMode.OnExplicitShutdown</c>へ
/// 一時的に切り替えることでこれを防ぐ。
///
/// このテストは<c>App.axaml.cs</c>個別のロジックではなく、その対処が前提とするAvalonia側の
/// 挙動そのもの（<c>ShutdownMode</c>によって「最後のウィンドウが閉じたときに自動で
/// <c>ShutdownRequested</c>を発火するかどうか」が変わること）を固定する
/// （<see cref="DesktopShutdownSemanticsTests"/>と同じ設計方針）。<c>App</c>自体はAvaloniaの
/// フル起動シーケンスに依存し単体テストしにくいため、実機/Xvfbでの確認と合わせて検証する
/// （本タスクの実機/Xvfb検証結果はコミットメッセージ参照）。
///
/// 【実装メモ: なぜ<c>SubscribeGlobalEvents</c>をリフレクションで直接呼ぶのか】
/// <c>ClassicDesktopStyleApplicationLifetime</c>は、生成しただけでは<c>Window</c>の
/// 開閉を一切監視しない。実際にウィンドウの開閉を監視できるのは
/// <c>Application.Current.ApplicationLifetime</c>に据えられ<c>Start()</c>が呼ばれたインスタンス
/// だけだが、<see cref="DialogKeyboardCoverageTests"/>のコメントにあるとおり、
/// <c>Application.Current.ApplicationLifetime</c>のsetterは一度初期化されると二度と変更できず、
/// headlessテスト環境では起動時に（<c>IClassicDesktopStyleApplicationLifetime</c>ではない）
/// 別の値へ既に初期化済みのため、テストからこの経路には乗せられない。
/// Avalonia.Controls.dll（11.2.3）を逆コンパイルして確認したところ、<c>Window</c>の開閉監視は
/// <c>Application.Current</c>を介さず、<c>Window.WindowOpenedEvent</c>/<c>WindowClosedEvent</c>
/// （型全体に対するクラスハンドラ）を購読する<c>internal</c>の<c>SubscribeGlobalEvents()</c>で
/// 行っている。このメソッドさえ呼んでおけば、<c>Application.Current</c>に据えていない
/// スタンドアロンの<c>ClassicDesktopStyleApplicationLifetime</c>インスタンスでも、プロセス内で
/// 実際に<c>Show()</c>・<c>Close()</c>されたウィンドウを正しく検知できる（本番の
/// <c>App.axaml.cs</c>が依存しているのもこの同じ仕組み）。<c>internal</c>のため
/// <see cref="MethodInfo"/>経由で呼ぶ。将来のAvalonia更新でこのメソッドが無くなった場合は
/// <see cref="EnsureWindowTrackingEnabled"/>が例外で失敗し、その場でテストの前提が崩れたことが
/// 分かるようにしている（黙って何も検証しないテストにはしない）。
/// </summary>
public sealed class MainWindowShutdownModeGuardTests : IDisposable
{
    private ClassicDesktopStyleApplicationLifetime? _lifetime;

    /// <summary>
    /// 生成したスタンドアロンの<see cref="ClassicDesktopStyleApplicationLifetime"/>を確実に破棄する。
    /// <c>SubscribeGlobalEvents</c>が購読する<c>Window.WindowOpenedEvent</c>/<c>WindowClosedEvent</c>は
    /// プロセス全体に対するクラスハンドラのため、破棄せずに残すと以後の（このテストクラスに
    /// 限らない）全テストのウィンドウ開閉がこのインスタンスにも配信され続けてしまう。
    /// </summary>
    public void Dispose()
    {
        _lifetime?.Dispose();
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "不具合修正の回帰: OnExplicitShutdownの間は、唯一のウィンドウを閉じてもShutdownRequestedは発火しない（メインウィンドウが立つ前のダイアログでアプリが終了しない）")]
    public void OnExplicitShutdownの間は唯一のウィンドウを閉じても発火しない()
    {
        var lifetime = CreateTrackingLifetime(ShutdownMode.OnExplicitShutdown);
        var raised = false;
        lifetime.ShutdownRequested += (_, _) => raised = true;

        // 復帰確認ダイアログ相当: メインウィンドウが無い状態で、開いているウィンドウが
        // これ1枚だけの状況を作り、それを閉じる。
        var dialog = new Window();
        dialog.Show();
        dialog.Close();

        raised.Should().BeFalse(
            "OnExplicitShutdownの間はウィンドウが0枚になっても自動でShutdownRequestedを" +
            "発火してはならない。これがApp.OnFrameworkInitializationCompletedがdesktop.MainWindowを" +
            "割り当てるまでの間、この値へ切り替えている理由そのもの");
        lifetime.Windows.Should().BeEmpty("ウィンドウ自体は通常どおり閉じられ、追跡対象から外れているはず");
    }

    [AvaloniaFact(DisplayName = "回帰の裏取り: OnLastWindowCloseへ戻した後は、唯一のウィンドウを閉じるとShutdownRequestedが発火する（×ボタンで閉じたら終了する、という既存の挙動を壊していないことの確認）")]
    public void OnLastWindowCloseへ戻すと唯一のウィンドウを閉じると発火する()
    {
        var lifetime = CreateTrackingLifetime(ShutdownMode.OnLastWindowClose);
        var raised = false;
        lifetime.ShutdownRequested += (_, e) =>
        {
            raised = true;
            e.Cancel = true; // 実際のOnShutdownRequestedと同じく、後始末のため一旦保留する想定。
        };

        // desktop.MainWindowを割り当てた後の、通常の×ボタンでの終了に相当する。
        var mainWindow = new Window();
        mainWindow.Show();
        mainWindow.Close();

        raised.Should().BeTrue(
            "App.OnFrameworkInitializationCompletedはdesktop.MainWindowを割り当てた直後に" +
            "ShutdownModeをOnLastWindowCloseへ戻す。戻し忘れるとウィンドウを閉じてもプロセスが" +
            "終了しなくなる（既存のOnShutdownRequested・トレイ格納の設定と噛み合わなくなる）ため、" +
            "戻した後に通常どおり発火することも合わせて固定する");
    }

    /// <summary>
    /// <see cref="ClassicDesktopStyleApplicationLifetime.SubscribeGlobalEvents"/>
    /// （クラス冒頭コメント参照）をリフレクションで呼び、実際のウィンドウ開閉を検知できる
    /// 状態にしてから返す。破棄は<see cref="Dispose"/>が担う。
    /// </summary>
    private ClassicDesktopStyleApplicationLifetime CreateTrackingLifetime(ShutdownMode shutdownMode)
    {
        var lifetime = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = shutdownMode };
        _lifetime = lifetime;

        var subscribeGlobalEvents = typeof(ClassicDesktopStyleApplicationLifetime)
            .GetMethod("SubscribeGlobalEvents", BindingFlags.NonPublic | BindingFlags.Instance);
        subscribeGlobalEvents.Should().NotBeNull(
            "Avalonia.Controls.ClassicDesktopStyleApplicationLifetime.SubscribeGlobalEventsが見つからない。" +
            "Avalonia更新でシグネチャ・名前が変わった可能性があるため、このテストの前提を見直すこと" +
            "（クラス冒頭コメント参照）");
        subscribeGlobalEvents!.Invoke(lifetime, null);

        return lifetime;
    }
}
