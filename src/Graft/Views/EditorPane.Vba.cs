using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// <see cref="EditorPane"/> の分割ファイル。Excel VBA 連携の「VBE でこの行を開く」（仕様書_VBA連携 7.6）。
/// <para>
/// 【右クリックした行を使う理由】 AvaloniaEdit は右クリックではキャレットを動かさないので、メニューの
/// 「この行」をキャレットの行にすると、右クリックした行と食い違う。右ボタンを押した位置を記録しておき、
/// その行を使う（位置が取れなければキャレットの行にする）。
/// </para>
/// <para>
/// 保存経路と <c>DocumentSession</c> には触れない（性能の約束）。ここで増えるのは右ボタンを押した瞬間の
/// 位置の記録だけで、文書の走査はしない。
/// </para>
/// </summary>
public partial class EditorPane
{
    // 右ボタンを押した位置の行（1 始まり）。取れなかったときは 0。
    private int _rightClickLine;

    private void InitializeVbaMenu()
    {
        // 本文の側が既に処理済み（Handled）でも位置は欲しいので、handledEventsToo で受ける。
        Editor.AddHandler(PointerPressedEvent, OnEditorPointerPressedForVbe, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnEditorPointerPressedForVbe(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(Editor);
        if (!point.Properties.IsRightButtonPressed) return;

        // TextEditor 自身の座標からの位置（行・列）。範囲外なら null。
        var position = Editor.GetPositionFromPoint(point.Position);
        _rightClickLine = position?.Line ?? 0;
    }

    /// <summary>
    /// 右クリックメニューを開く直前に、「VBE でこの行を開く」の表示を決める。Excel VBA プロジェクトのミラー内の
    /// ファイルのときだけ出す（それ以外では項目も区切り線も出さない）。
    /// </summary>
    private void UpdateShowInVbeMenu()
    {
        var visible = _viewModel is { ActiveTab: { Kind: EditorTabKind.Document } tab }
                      && _viewModel.CanShowInVbe?.Invoke(tab.Session.RelativePath) == true;
        ShowInVbeMenuItem.IsVisible = visible;
        ShowInVbeSeparator.IsVisible = visible;
    }

    private async void OnShowInVbeClicked(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not { ActiveTab: { Kind: EditorTabKind.Document } tab, ShowInVbeAsync: { } open }) return;

        var line = _rightClickLine > 0 ? _rightClickLine : Editor.TextArea.Caret.Line;
        await SafeHandler.RunAsync("VBE で開く", () => open(tab.Session.RelativePath, line)).ConfigureAwait(true);
    }
}
