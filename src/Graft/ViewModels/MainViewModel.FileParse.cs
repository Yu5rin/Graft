using Graft.Core;
using Graft.Editor;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> の分割ファイル（1ファイル400行上限のため）。
/// 仕様書4.1「ファイルからのパッチ解析」。AIの出力をクリップボード経由だけでなく、
/// .md/.txt/.diff/.patch 等に保存したファイルからも解析できるようにする。
/// ファイル選択ダイアログ（<see cref="PickAndParseFileAsync"/>）と、接ぎ木パネルへの
/// ドラッグ＆ドロップ（<see cref="LoadPatchFromFileAsync"/>をViewが直接呼ぶ）の両方の入口を
/// 持つが、検証と読み込みは<see cref="LoadPatchFromFileAsync"/>に一本化し、最終的には
/// MainViewModel.cs の共有解析経路（ParseTextAndLoadAsync）へ合流する。
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>ファイル選択ダイアログで優先的に提示する拡張子。AIの出力が保存されがちな形式。</summary>
    private static readonly IReadOnlyList<string> PatchFileExtensions = new[] { ".md", ".txt", ".diff", ".patch" };

    /// <summary>ファイルからの解析で受け付ける最大サイズ（1MB）。超過分は明確なエラーとして拒否する。</summary>
    private const long MaxPatchFileSizeBytes = 1024 * 1024;

    /// <summary>「ファイルを選んで解析する」コマンド（接ぎ木パネルの空状態ボタン等）。</summary>
    private async Task PickAndParseFileAsync()
    {
        var path = await _dialogs.PickFileAsync("パッチファイルを選択", PatchFileExtensions).ConfigureAwait(true);
        if (string.IsNullOrEmpty(path)) return;

        await LoadPatchFromFileAsync(path).ConfigureAwait(true);
    }

    /// <summary>
    /// ファイルからの解析の本体。ファイル選択ダイアログ経由と、接ぎ木パネルへのドラッグ＆ドロップ
    /// （GraftPanel.axaml.cs）経由の両方から呼ばれるため公開している。バイナリ・1MB超のファイルは
    /// 解析を行わず、中央ペインへ理由付きのエラーとして表示する。
    /// </summary>
    public async Task LoadPatchFromFileAsync(string filePath)
    {
        var issue = await ValidatePatchFileAsync(filePath).ConfigureAwait(true);
        if (issue is not null)
        {
            CenterError = issue;
            State = CenterPaneState.Error;
            return;
        }

        var read = await FileTextIO.ReadAsync(filePath).ConfigureAwait(true);
        if (!read.IsSuccess)
        {
            CenterError = read.Errors.FirstOrDefault();
            State = CenterPaneState.Error;
            return;
        }

        await ParseTextAndLoadAsync(read.Value.Text).ConfigureAwait(true);
    }

    /// <summary>
    /// 選択・ドロップされたファイルが解析可能かを検証する。存在しない・1MB超・バイナリの
    /// いずれかであれば問題を返す（問題無しはnull）。バイナリ判定は<see cref="DocumentSession"/>の
    /// 先頭サンプリング判定（E703）を再利用する。
    /// </summary>
    private static async Task<GraftIssue?> ValidatePatchFileAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return GraftIssue.Of(ErrorCode.E204, "ファイルが見つかりません。パスを確認してください。", path: filePath);
        }

        var length = new FileInfo(filePath).Length;
        if (length > MaxPatchFileSizeBytes)
        {
            return GraftIssue.Of(ErrorCode.E203, "1MBを超えるファイルは読み込めません。", path: filePath);
        }

        if (await DocumentSession.LooksBinaryAsync(filePath, CancellationToken.None).ConfigureAwait(false))
        {
            return GraftIssue.Of(ErrorCode.E703, "バイナリファイルは読み込めません。テキストファイルを選択してください。", path: filePath);
        }

        return null;
    }
}
