namespace Graft.Platform.Null;

/// <summary>
/// <see cref="IDialogService"/> の何もしない実装。テスト・未対応環境向け。
///
/// ユーザーに尋ねられない状況（headlessテストや未対応環境）で「はい」と答えてしまうと
/// 破壊的操作（未保存の変更の破棄・上書き等）が黙って進んでしまうため、確認系は
/// すべてキャンセル扱い（安全側）で応答する。通知（<see cref="ShowMessageAsync"/>）だけは
/// 応答を持たないため何もしない。
/// </summary>
public sealed class NullDialogService : IDialogService
{
    public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);

    public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
        => Task.FromResult((bool?)null);

    public Task<string?> PromptAsync(string title, string message, string? initial = null)
        => Task.FromResult((string?)null);

    public Task<string?> PickFolderAsync(string title) => Task.FromResult((string?)null);

    public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult((string?)null);

    public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
}
