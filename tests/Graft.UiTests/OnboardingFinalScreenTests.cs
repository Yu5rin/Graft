using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Infra;
using Graft.UiTests.TestSupport;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 初回起動ガイド（<see cref="OnboardingWindow"/>）の最終画面（Screen4）の回帰テスト。
/// 利用者からの指摘「接ぎ木が体験できないので、ソフトの中核を体験できない」への対応として、
/// 従来「完了」ボタン1つだった最終画面を「使い方を学ぶ」「チュートリアルを終了」の2択に
/// 変更した。<see cref="OnboardingWindow.StartTutorialRequested"/>が、この2択とスキップ・Escの
/// 組み合わせで正しく立つ／立たないことを検証する（実際の画面上チュートリアル本体の進行は
/// TutorialTests.cs側で検証する。このウィンドウ自体はシェルの実コントロールを知らないため）。
/// </summary>
public class OnboardingFinalScreenTests : IDisposable
{
    private readonly string _baseDirectory =
        Path.Combine(Path.GetTempPath(), "graft-onboarding-final-screen-tests", Guid.NewGuid().ToString("N"));

    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        _windows.Dispose();

        try
        {
            if (Directory.Exists(_baseDirectory)) Directory.Delete(_baseDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗は検証結果と無関係のため無視する。
        }

        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "最終画面まで進むと「使い方を学ぶ」「チュートリアルを終了」の2択が表示され、下段のNext/Skipは隠れる")]
    public async Task 最終画面の2択が表示される()
    {
        var onboarding = _windows.Track(new OnboardingWindow(new AppPaths(_baseDirectory)));
        onboarding.Show();

        RaiseClick(onboarding, "次へ"); // 画面1→画面2
        await SettleAsync().ConfigureAwait(true);
        RaiseClick(onboarding, "次へ"); // 画面2→画面3
        await SettleAsync().ConfigureAwait(true);
        RaiseClick(onboarding, "次へ"); // 画面3→画面4（最終選択）
        await SettleAsync().ConfigureAwait(true);

        var buttons = onboarding.GetVisualDescendants().OfType<Button>().ToList();
        buttons.Should().Contain(b => Equals(b.Content, "使い方を学ぶ") && b.IsVisible);
        buttons.Should().Contain(b => Equals(b.Content, "チュートリアルを終了") && b.IsVisible);

        var nextButton = buttons.Single(b => Equals(b.Content, "次へ"));
        var skipButton = buttons.Single(b => Equals(b.Content, "スキップ"));
        nextButton.IsVisible.Should().BeFalse("最終画面では画面内の2択に一本化し、下段の次へは隠す");
        skipButton.IsVisible.Should().BeFalse("最終画面では画面内の2択に一本化し、下段のスキップは隠す");
    }

    [AvaloniaFact(DisplayName = "「チュートリアルを終了」を選ぶとStartTutorialRequestedはfalseのまま完了マーカーが書かれて閉じる")]
    public async Task チュートリアルを終了を選ぶとStartTutorialRequestedはfalse()
    {
        var appPaths = new AppPaths(_baseDirectory);
        var onboarding = _windows.Track(new OnboardingWindow(appPaths));
        onboarding.Show();

        RaiseClick(onboarding, "次へ");
        await SettleAsync().ConfigureAwait(true);
        RaiseClick(onboarding, "次へ");
        await SettleAsync().ConfigureAwait(true);
        RaiseClick(onboarding, "次へ");
        await SettleAsync().ConfigureAwait(true);

        var closed = false;
        onboarding.Closed += (_, _) => closed = true;
        RaiseClick(onboarding, "チュートリアルを終了");
        await SettleAsync().ConfigureAwait(true);

        closed.Should().BeTrue();
        onboarding.StartTutorialRequested.Should().BeFalse();
        OnboardingWindow.HasCompleted(appPaths).Should().BeTrue("「チュートリアルを終了」でも表示済みフラグは書き出される必要がある");
    }

    [AvaloniaFact(DisplayName = "「使い方を学ぶ」を選ぶとStartTutorialRequestedがtrueになり完了マーカーも書かれて閉じる")]
    public async Task 使い方を学ぶを選ぶとStartTutorialRequestedはtrue()
    {
        var appPaths = new AppPaths(_baseDirectory);
        var onboarding = _windows.Track(new OnboardingWindow(appPaths));
        onboarding.Show();

        RaiseClick(onboarding, "次へ");
        await SettleAsync().ConfigureAwait(true);
        RaiseClick(onboarding, "次へ");
        await SettleAsync().ConfigureAwait(true);
        RaiseClick(onboarding, "次へ");
        await SettleAsync().ConfigureAwait(true);

        var closed = false;
        onboarding.Closed += (_, _) => closed = true;
        RaiseClick(onboarding, "使い方を学ぶ");
        await SettleAsync().ConfigureAwait(true);

        closed.Should().BeTrue();
        onboarding.StartTutorialRequested.Should().BeTrue();
        OnboardingWindow.HasCompleted(appPaths).Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "スキップは最終選択画面を経由せず、当然StartTutorialRequestedもfalseのまま終了する")]
    public async Task スキップではStartTutorialRequestedはfalseのまま終了する()
    {
        var appPaths = new AppPaths(_baseDirectory);
        var onboarding = _windows.Track(new OnboardingWindow(appPaths));
        onboarding.Show();

        // 画面1（導入）から、途中の画面をまったく経由せずスキップする。
        RaiseClick(onboarding, "スキップ");
        await SettleAsync().ConfigureAwait(true);

        onboarding.StartTutorialRequested.Should().BeFalse();
        OnboardingWindow.HasCompleted(appPaths).Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "Escで閉じた場合も最終選択画面を経由せず、StartTutorialRequestedはfalseのまま終了する")]
    public async Task Escで閉じた場合もStartTutorialRequestedはfalseのまま終了する()
    {
        var appPaths = new AppPaths(_baseDirectory);
        var onboarding = _windows.Track(new OnboardingWindow(appPaths));
        onboarding.Show();

        RaiseClick(onboarding, "次へ"); // 画面1→画面2（途中の画面からのEscも確認する）
        await SettleAsync().ConfigureAwait(true);

        var closed = false;
        onboarding.Closed += (_, _) => closed = true;
        onboarding.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await SettleAsync().ConfigureAwait(true);

        closed.Should().BeTrue();
        onboarding.StartTutorialRequested.Should().BeFalse();
        OnboardingWindow.HasCompleted(appPaths).Should().BeTrue();
    }

    private static void RaiseClick(Window window, string content)
    {
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, content));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static async Task SettleAsync()
    {
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(10);
        }
    }
}
