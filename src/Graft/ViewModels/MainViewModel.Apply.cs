using System.IO;
using Graft.Core;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> の分割ファイル（1ファイル400行上限のため）。
/// 仕様書4.8/7章「接ぎ木との連携」のうち、適用前チェック（未保存確認）と適用後の再読込通知を
/// 担う。MainViewModelはUI層（AvalonEdit・エディタタブ）の型を知らないため、実際に
/// エディタと結ぶのはShellViewModel（<see cref="BeforeApplyAsync"/>/<see cref="AfterApplyAsync"/>
/// へデリゲートを設定する。附録A）。
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// 4.8/7章 適用前チェック用フック。ドライラン開始時に対象ファイルの絶対パス一覧を渡して呼ぶ。
    /// falseが返るとドライランを中止する。
    /// </summary>
    public Func<IReadOnlyList<string>, Task<bool>>? BeforeApplyAsync { get; set; }

    /// <summary>
    /// 4.8/7章 適用後フック。適用が成功したら、書き換わったファイルの絶対パス一覧を渡して必ず呼ぶ。
    /// </summary>
    public Func<IReadOnlyList<string>, Task>? AfterApplyAsync { get; set; }

    /// <summary>RunDryRunAsyncの冒頭から呼ぶ。フック未設定時は常にtrue。</summary>
    private async Task<bool> ConfirmTargetsSavedAsync(string projectRoot)
    {
        if (BeforeApplyAsync is null) return true;
        return await BeforeApplyAsync(ResolveTargetFullPaths(projectRoot)).ConfigureAwait(true);
    }

    /// <summary>ApplyAsync成功直後から呼ぶ。フック未設定時は何もしない。</summary>
    private async Task NotifyFilesRewrittenAsync(string projectRoot, RevisionManifest manifest)
    {
        if (AfterApplyAsync is null) return;

        var files = manifest.Entries
            .Select(entry => Path.Combine(projectRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar)))
            .Distinct()
            .ToList();
        await AfterApplyAsync(files).ConfigureAwait(true);
    }

    /// <summary>_currentPatchのブロックから対象ファイルの絶対パス一覧（重複除去）を求める。</summary>
    private IReadOnlyList<string> ResolveTargetFullPaths(string projectRoot)
    {
        if (_currentPatch is null) return Array.Empty<string>();
        return _currentPatch.Blocks
            .Select(b => Path.Combine(projectRoot, b.Path.Replace('/', Path.DirectorySeparatorChar)))
            .Distinct()
            .ToList();
    }
}
