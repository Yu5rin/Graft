using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FluentAssertions;
using Graft.Platform;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 実機（Windows）不具合の回帰: Excel VBA 連携のプロジェクトを削除しようとすると、2枚続く確認の2枚目
/// （ミラーと同期状態の扱い）が、操作する前に閉じて「キャンセル」扱いになり、何も削除されなかった。
///
/// 【原因】 <see cref="AvaloniaDialogService"/> は、結果を返した（<c>TrySetResult</c>）あとで窓を閉じていた。
/// ダイアログを <c>await</c> していたコードの続きは、同じ UI スレッドで、結果を返した瞬間にその場で先に動く。
/// 続きが次のダイアログを出すと、親は「表示中でアクティブな窓」から選ばれ、まだ閉じていない1枚目が親になる。
/// そのあと1枚目が閉じると、子の2枚目も道連れで閉じて「キャンセル（null）」を返していた。通常のプロジェクトは
/// 確認が1枚だけなので、VBA のプロジェクトだけが影響を受けた。
///
/// 【このテストの確かめ方】 ヘッドレスには親子にするためのデスクトップのライフタイムが無く、実機の「2枚目が道連れで
/// 閉じる」ところまでは再現できない。そこで原因そのもの、すなわち「続きが動く時点で、1枚目の窓がもう閉じているか」を
/// 確かめる（直す前は、続きの時点で窓が表示中のままで失敗する）。
/// </summary>
public class AvaloniaDialogServiceChainedDialogTests
{
    /// <summary>ダイアログの種類。いずれも結果を返すと窓を閉じ、続きが動く。</summary>
    public enum Kind { Confirm, ConfirmDestructive, ConfirmThreeWay, Prompt }

    [AvaloniaTheory(DisplayName = "実機不具合の回帰: ダイアログの結果を待っていた続きは、そのダイアログの窓が閉じたあとに動く（続けて次のダイアログを出しても巻き込まれない）")]
    [InlineData(Kind.Confirm)]
    [InlineData(Kind.ConfirmDestructive)]
    [InlineData(Kind.ConfirmThreeWay)]
    [InlineData(Kind.Prompt)]
    public async Task 続きは窓が閉じたあとに動く(Kind kind)
    {
        var service = new AvaloniaDialogService();
        Window? first = null;
        Button? firstAction = null;
        AvaloniaDialogService.OnButtonRowBuiltForTests = (window, buttons) =>
        {
            if (first is not null) return;
            first = window;
            firstAction = buttons[0]; // 左端（肯定・実行）のボタン。
        };

        try
        {
            bool? visibleWhenContinued = null;

            async Task FlowAsync()
            {
                switch (kind)
                {
                    case Kind.Confirm: await service.ConfirmAsync("題名", "本文"); break;
                    case Kind.ConfirmDestructive: await service.ConfirmDestructiveAsync("題名", "本文", "削除する"); break;
                    case Kind.ConfirmThreeWay: await service.ConfirmThreeWayAsync("題名", "本文", "はい", "いいえ"); break;
                    case Kind.Prompt: await service.PromptAsync("題名", "本文", "初期値"); break;
                }

                // 続き。ここで次のダイアログを出すと、親は「表示中でアクティブな窓」から選ばれる。1枚目がまだ表示中だと、
                // 1枚目が閉じるときに道連れで閉じてしまう。
                visibleWhenContinued = first!.IsVisible;
            }

            var flow = FlowAsync();
            Dispatcher.UIThread.RunJobs();
            first.Should().NotBeNull("ダイアログが表示されているはず");

            firstAction!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await flow;

            visibleWhenContinued.Should().BeFalse(
                "続きが動く時点で、結果を返した窓はもう閉じている必要がある（表示中のままだと、続けて出した次のダイアログがその窓の子になり、閉じるときに道連れで閉じてキャンセル扱いになる）");
        }
        finally
        {
            AvaloniaDialogService.OnButtonRowBuiltForTests = null;
        }
    }

    [AvaloniaFact(DisplayName = "実機不具合の回帰: 3択の確認を2枚続けて出しても、2枚目は自分で選んだ結果を返す（プロジェクト削除の確認の並び）")]
    public async Task 続けて出した2枚目は選んだ結果を返す()
    {
        var service = new AvaloniaDialogService();
        var windows = new List<Window>();
        var rows = new List<IReadOnlyList<Button>>();
        AvaloniaDialogService.OnButtonRowBuiltForTests = (window, buttons) =>
        {
            windows.Add(window);
            rows.Add(buttons);
        };

        try
        {
            bool? first = null;
            bool? second = null;

            async Task FlowAsync()
            {
                // プロジェクトの削除と同じ並び: 履歴の確認 → ミラーと同期状態の確認。
                first = await service.ConfirmThreeWayAsync("プロジェクトを削除", "本文", "履歴は残す", "履歴も削除する");
                second = await service.ConfirmThreeWayAsync("ミラーと同期状態の扱い", "本文", "登録だけ外す", "ミラーと同期状態も削除する");
            }

            var flow = FlowAsync();
            Dispatcher.UIThread.RunJobs();

            rows[0][0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); // 1枚目: 履歴は残す。
            for (var i = 0; i < 50 && rows.Count < 2; i++)
            {
                await Task.Delay(10);
                Dispatcher.UIThread.RunJobs();
            }

            rows.Should().HaveCount(2, "1枚目に答えたあとで、2枚目が表示されるはず");
            windows[1].IsVisible.Should().BeTrue("2枚目は、1枚目が閉じたあとも利用者が答えるまで開いたままのはず");
            second.Should().BeNull("利用者がまだ答えていない");

            rows[1][1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); // 2枚目: ミラーと同期状態も削除する（false）。
            await flow;

            first.Should().BeTrue();
            second.Should().BeFalse("2枚目で選んだ「ミラーと同期状態も削除する」が、キャンセル（null）にならずに返るはず");
        }
        finally
        {
            AvaloniaDialogService.OnButtonRowBuiltForTests = null;
        }
    }
}
