using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.UiTests.TestSupport;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 細かいユーザビリティ改善5: 独立したウィンドウ（設定・ショートカット一覧・適用前プレビュー・
/// パッチキュー・コンテキスト収集・ログビューア・オープンソースライセンス・初回起動ガイド）
/// について、「Enterで肯定的な既定ボタンが実行される」「Escでキャンセル・閉じる」「開いた直後の
/// フォーカスが適切な位置にある」の3点を機械的に検証する。<see cref="SettingsHelpTipCoverageTests"/>
/// と同じ「対象を列挙して検査する」網羅テストの形にし、新しいウィンドウを追加した際に3点の
/// いずれかを付け忘れても気付けるようにする。
///
/// 【AvaloniaDialogServiceが組み立てる確認・3択・入力・メッセージダイアログについて】
/// この4種はコードレビューで3点とも元から揃っていることを確認済み（IsDefault・IsCancel・
/// Loadedでの初期フォーカスがすべてのメソッドに実装されている）。しかし自動テストとしての
/// 網羅は見送った: これらは<see cref="Graft.Platform.AvaloniaDialogService"/>内部で動的に
/// <c>Window</c>を生成し、外部へは<c>Task&lt;T&gt;</c>しか返さないため、実際に表示された
/// ダイアログへ外部からアクセスする手段が無い。<c>FindOwnerWindow</c>にオーナーを見つけさせて
/// <c>ShowDialog(owner)</c>経路（<c>owner.OwnedWindows</c>経由でアクセス可能）に乗せるには
/// <c>Application.Current.ApplicationLifetime</c>を<c>IClassicDesktopStyleApplicationLifetime</c>
/// にする必要があるが、実際に試したところ<c>Avalonia.Application.ApplicationLifetime</c>の
/// setterは「一度初期化されたら二度と変更できない」実装（<c>InvalidOperationException</c>）で、
/// かつheadlessテスト環境では起動時に別の値へ既に初期化済みだった。この制約はAvalonia側の
/// 仕様であり本体コード側では回避できないため、この4種は自動テストでの網羅を諦め、
/// コードレビューでの確認に留める（判断の根拠として本コメントを残す）。
/// </summary>
public class DialogKeyboardCoverageTests : IDisposable
{
    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        _windows.Dispose();
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "ショートカット一覧: 閉じるが既定ボタン・初期フォーカス、Escで閉じる")]
    public void ショートカット一覧のキー操作が揃っている()
    {
        var window = _windows.Track(new ShortcutsWindow());
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var close = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "閉じる"));
        close.IsDefault.Should().BeTrue();
        close.IsFocused.Should().BeTrue();
        AssertEscapeCloses(window);
    }

    [AvaloniaFact(DisplayName = "適用前プレビュー: 適用が既定ボタン・初期フォーカス、キャンセルがIsCancel、Escで閉じる")]
    public void 適用前プレビューのキー操作が揃っている()
    {
        var window = _windows.Track(new ApplyPreviewWindow());
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var apply = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "適用"));
        var cancel = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "キャンセル"));
        apply.IsDefault.Should().BeTrue();
        cancel.IsCancel.Should().BeTrue();
        apply.IsFocused.Should().BeTrue();
        AssertEscapeCloses(window);
    }

    [AvaloniaFact(DisplayName = "パッチキュー: 結合して適用へ進むが既定ボタン、Escで閉じる")]
    public void パッチキューのキー操作が揃っている()
    {
        var window = _windows.Track(new QueueWindow());
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var merge = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "結合して適用へ進む"));
        merge.IsDefault.Should().BeTrue();
        // キューが空の間はMergeCommandが無効（IsEffectivelyEnabled=false）なため、
        // 閉じるボタンへ初期フォーカスが逃げる（QueueWindow.axaml.cs参照）。
        // ここでは既定コンストラクタ（DataContext無し）で構築するため常に空のキュー扱いになり、
        // 「閉じる」側にフォーカスが当たることを確認する。
        var close = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "閉じる"));
        close.IsFocused.Should().BeTrue("既定ボタンが無効な間は「閉じる」へ初期フォーカスが必要");
        AssertEscapeCloses(window);
    }

    [AvaloniaFact(DisplayName = "コンテキスト収集: 初期フォーカスは収集モード、Escで閉じる")]
    public void コンテキスト収集のキー操作が揃っている()
    {
        var window = _windows.Track(new ContextCollectWindow());
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var modeCombo = window.GetVisualDescendants().OfType<ComboBox>()
            .Single(c => Avalonia.Automation.AutomationProperties.GetName(c) == "収集モード");
        modeCombo.IsFocused.Should().BeTrue();
        AssertEscapeCloses(window);
    }

    [AvaloniaFact(DisplayName = "ログビューア: 閉じるが既定ボタン・初期フォーカス、Escで閉じる")]
    public void ログビューアのキー操作が揃っている()
    {
        var window = _windows.Track(new LogViewerWindow("test.log", "ログの内容"));
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var close = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "閉じる"));
        close.IsDefault.Should().BeTrue();
        close.IsFocused.Should().BeTrue();
        AssertEscapeCloses(window);
    }

    [AvaloniaFact(DisplayName = "オープンソースライセンス: 閉じるが既定ボタン・初期フォーカス、Escで閉じる")]
    public void オープンソースライセンスのキー操作が揃っている()
    {
        var window = _windows.Track(new OpenSourceLicensesWindow());
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var close = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "閉じる"));
        close.IsDefault.Should().BeTrue();
        close.IsFocused.Should().BeTrue();
        AssertEscapeCloses(window);
    }

    [AvaloniaFact(DisplayName = "設定: 初期フォーカスは最初のカテゴリタブ、Escで閉じる")]
    public void 設定ウィンドウのキー操作が揃っている()
    {
        var window = _windows.Track(new SettingsWindow());
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // TabControl自体は既定でFocusable=falseのため、選択中の最初のTabItemを対象にする
        // （SettingsWindow.axaml.cs参照）。
        var firstTab = window.GetVisualDescendants().OfType<TabItem>().First();
        firstTab.IsFocused.Should().BeTrue();
        AssertEscapeCloses(window);
    }

    [AvaloniaFact(DisplayName = "初回起動ガイド: 次へが既定ボタン・初期フォーカス、Escで終了する")]
    public void 初回起動ガイドのキー操作が揃っている()
    {
        var window = _windows.Track(new OnboardingWindow());
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var next = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "次へ"));
        next.IsDefault.Should().BeTrue();
        next.IsFocused.Should().BeTrue();
        // このウィンドウはEscで「キャンセル」ではなく「（スキップ扱いで）終了」する設計
        // （OnboardingWindow.axaml.cs OnTunnelKeyDown参照。ウィザードに「元に戻す変更」が
        // 無いため、Cancel概念の代わりに完了扱いとする判断）。
        AssertEscapeCloses(window);
    }

    private static void AssertEscapeCloses(Window window)
    {
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        closed.Should().BeTrue("Escでこのウィンドウを閉じられる必要がある");
    }
}
