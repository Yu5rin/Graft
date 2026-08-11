using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FluentAssertions;
using Graft.Platform;

namespace Graft.UiTests;

/// <summary>
/// 不具合2の回帰: 実機（Windows）で、未保存の変更を確認するダイアログのボタンが
/// 「キャンセル」「破棄」「保存」の順（左から）で表示され、Windowsの作法（メモ帳・Office・
/// VS Codeはいずれも肯定的な選択肢が左）と逆になっていた。原因は
/// <see cref="AvaloniaDialogService"/>の<see cref="AvaloniaDialogService.ConfirmAsync"/>・
/// <see cref="AvaloniaDialogService.ConfirmThreeWayAsync"/>・<see cref="AvaloniaDialogService.PromptAsync"/>
/// がボタンを「キャンセル→否定→肯定」の順で<c>StackPanel.Children</c>へ追加していたこと
/// （<see cref="AvaloniaDialogService"/>のクラスコメント参照）。
///
/// これら3メソッドは動的に<c>Window</c>を組み立てるだけで外部へは<c>Task&lt;T&gt;</c>しか
/// 返さないため、実際に表示されたダイアログへ外部（テスト）からアクセスする手段が無い
/// （<see cref="DialogKeyboardCoverageTests"/>のコメントに同じ制約の記載がある）。そこで
/// <see cref="AvaloniaDialogService.OnButtonRowBuiltForTests"/>（このバグ修正で追加したテスト専用の
/// 差し替え口）を使い、実際に組み立てられたボタン一覧を左から並んだ順で捕捉して検証する。
/// </summary>
public class AvaloniaDialogServiceButtonOrderTests
{
    [AvaloniaFact(DisplayName = "実機不具合の回帰: ConfirmAsyncは「OK」「キャンセル」の順（肯定が左）で並び、OKが既定ボタン")]
    public void ConfirmAsyncのボタン順序と既定ボタンが正しい()
    {
        RunWithCapturedButtons(
            () => new AvaloniaDialogService().ConfirmAsync("タイトル", "メッセージ"),
            buttons =>
            {
                buttons.Select(b => b.Content).Should().Equal(new object?[] { "OK", "キャンセル" },
                    "Windowsの作法どおり肯定的な選択肢（OK）が左、キャンセルが右に来る必要がある");
                buttons[0].IsDefault.Should().BeTrue("OKがEnterで実行される既定ボタンのはず");
                buttons[1].IsCancel.Should().BeTrue("キャンセルがEscで実行されるはず");
            });
    }

    [AvaloniaFact(DisplayName = "実機不具合の回帰: ConfirmThreeWayAsyncは「肯定」「否定」「キャンセル」の順で並ぶ")]
    public void ConfirmThreeWayAsyncのボタン順序が正しい()
    {
        RunWithCapturedButtons(
            () => new AvaloniaDialogService().ConfirmThreeWayAsync("タイトル", "メッセージ", "はい", "いいえ"),
            buttons =>
            {
                buttons.Select(b => b.Content).Should().Equal(new object?[] { "はい", "いいえ", "キャンセル" },
                    "実機不具合の回帰確認: 以前は「キャンセル」「いいえ」「はい」の逆順だった");
                buttons[0].IsDefault.Should().BeTrue("yesLabel（肯定）が既定ボタンのはず");
                buttons[2].IsCancel.Should().BeTrue();
            });
    }

    [AvaloniaFact(DisplayName = "実機不具合の回帰: PromptAsyncは「OK」「キャンセル」の順で並ぶ")]
    public void PromptAsyncのボタン順序が正しい()
    {
        RunWithCapturedButtons(
            () => new AvaloniaDialogService().PromptAsync("タイトル", "メッセージ"),
            buttons =>
            {
                buttons.Select(b => b.Content).Should().Equal(new object?[] { "OK", "キャンセル" });
                buttons[0].IsDefault.Should().BeTrue();
                buttons[1].IsCancel.Should().BeTrue();
            });
    }

    /// <summary>
    /// <see cref="AvaloniaDialogService.OnButtonRowBuiltForTests"/>を使い、<paramref name="invoke"/>が
    /// 組み立てたボタン一覧（左から並んだ順）を<paramref name="assert"/>へ渡す。テスト間で
    /// 静的フックが残らないよう必ずnullへ戻し、開いたウィンドウも後始末する
    /// （<see cref="ShowModal"/>はheadlessテスト環境ではオーナーが見つからず非モーダルの
    /// <c>Show()</c>へ縮退するため、閉じ忘れるとレイアウトが保留したまま残る）。
    /// </summary>
    private static void RunWithCapturedButtons(Func<Task> invoke, Action<IReadOnlyList<Button>> assert)
    {
        Window? capturedWindow = null;
        IReadOnlyList<Button>? capturedButtons = null;
        AvaloniaDialogService.OnButtonRowBuiltForTests = (window, buttons) =>
        {
            capturedWindow = window;
            capturedButtons = buttons;
        };

        try
        {
            _ = invoke(); // フック呼び出しまでは同期的に進むため、Taskの完了を待つ必要は無い。
            capturedButtons.Should().NotBeNull("OnButtonRowBuiltForTestsが呼ばれているはず");
            assert(capturedButtons!);
        }
        finally
        {
            AvaloniaDialogService.OnButtonRowBuiltForTests = null;
            capturedWindow?.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
