using FluentAssertions;
using Graft.Platform;
using Graft.Platform.Null;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 仕様書19章・20章（フェーズL3）: ViewModel層をWPF/Avalonia間で共有するための抽象
/// <see cref="IDialogService"/> のうち、実UIを起動せずに検証できる
/// <see cref="NullDialogService"/>（テスト・未対応環境向けの何もしない実装）を扱う。
///
/// ユーザーに尋ねられない状況（headlessテストや未対応環境）で確認ダイアログが「はい」相当を
/// 返してしまうと、未保存の変更の破棄・設定の上書きインポートといった破壊的操作が
/// 黙って進んでしまう。そのため既定応答が安全側（キャンセル扱い）であることを
/// 契約として固定する。
/// </summary>
public class DialogServiceContractTests
{
    [Fact(DisplayName = "ConfirmAsyncは既定でfalse（キャンセル扱い）を返す")]
    public async Task ConfirmAsyncは既定でfalseを返す()
    {
        var sut = new NullDialogService();

        var result = await sut.ConfirmAsync("タイトル", "メッセージ");

        result.Should().BeFalse();
    }

    [Fact(DisplayName = "ConfirmThreeWayAsyncは既定でnull（キャンセル扱い）を返す")]
    public async Task ConfirmThreeWayAsyncは既定でnullを返す()
    {
        var sut = new NullDialogService();

        var result = await sut.ConfirmThreeWayAsync("タイトル", "メッセージ", "保存", "破棄");

        result.Should().BeNull();
    }

    [Fact(DisplayName = "PromptAsyncは既定でnull（キャンセル扱い）を返す")]
    public async Task PromptAsyncは既定でnullを返す()
    {
        var sut = new NullDialogService();

        var result = await sut.PromptAsync("タイトル", "メッセージ", "初期値");

        result.Should().BeNull();
    }

    [Fact(DisplayName = "PickFolderAsyncは既定でnull（キャンセル扱い）を返す")]
    public async Task PickFolderAsyncは既定でnullを返す()
    {
        var sut = new NullDialogService();

        var result = await sut.PickFolderAsync("フォルダを選択");

        result.Should().BeNull();
    }

    [Fact(DisplayName = "ShowMessageAsyncは例外を投げず完了する")]
    public async Task ShowMessageAsyncは例外を投げず完了する()
    {
        var sut = new NullDialogService();

        var act = async () => await sut.ShowMessageAsync("タイトル", "メッセージ");

        await act.Should().NotThrowAsync();
    }
}
