using Graft.Core;
using Graft.Core.Vba;

namespace Graft.Features;

/// <summary>
/// リボン（customUI）の取得（仕様書_ブックの情報と依頼の観点 15.2〜15.4）。保存済みのブックのファイルを読み取り専用で開いて
/// customUI の部品を取り出し（<see cref="VbaRibbonPackageReader"/>）、XML を解析し（<see cref="VbaRibbonXmlAnalyzer"/>）、
/// コールバックの名前を VBA のミラーと照合する（<see cref="VbaRibbonCallbackMatcher"/>）。
/// <para>
/// 【失敗しても例外にしない】 ファイルが無い・壊れている・ロックされている・customUI が無い、のいずれでも、結果の種類と理由で返す
/// （収集を止めないため。<c>FetchWorkbookInfoAsync</c> と同じ作法）。
/// </para>
/// <para>
/// 【ミラーの読み取りは、ブックの情報の取得と同じ契機】 Pull の直後に呼ばれるので、ミラーは Excel の最新に近い。ミラーを
/// 読めなかったときは、コールバックの照合だけを「できていません」にして続ける（リボンの構成は出す）。
/// </para>
/// </summary>
public static class VbaRibbonCollector
{
    /// <summary>ミラーの 1 ファイルを読む最大のバイト数（VBA のモジュールは 64 KB が上限なので、これを超えるものは異常）。</summary>
    private const long MaxMirrorFileBytes = 2 * 1024 * 1024;

    /// <summary>
    /// ブックのファイルからリボンを読んで取得結果にする。
    /// </summary>
    /// <param name="workbookPath">ブックのフルパス（<c>VbaLink.WorkbookFullName</c>）。</param>
    /// <param name="mirrorRoot">VBA のミラーのフォルダ（<c>Project.Root</c>）。コールバックの照合に使う。</param>
    /// <param name="limits">読む大きさの上限（null なら既定）。</param>
    /// <param name="ct">取り消し。</param>
    public static async Task<VbaRibbonSnapshot> CollectAsync(
        string workbookPath, string mirrorRoot, VbaRibbonLimits? limits = null, CancellationToken ct = default)
    {
        limits ??= new VbaRibbonLimits();
        // Windows のパス（\ 区切り）が来ても、ファイル名だけにする（区切りを先にそろえるので、Linux のテストでも同じ結果になる）。
        var name = Path.GetFileName(workbookPath.Replace('\\', '/'));

        // ファイルの読み取りと zip の展開は、ブックが大きいと時間がかかりうる。UI スレッドを塞がないよう、スレッドプールへ逃がす。
        var read = await Task.Run(() => VbaRibbonPackageReader.Read(workbookPath, limits), ct).ConfigureAwait(false);
        var now = DateTimeOffset.Now;
        if (read.Status != VbaRibbonReadStatus.Ok)
        {
            return new VbaRibbonSnapshot(
                name, workbookPath, now, read.FileModified, read.Status, read.Reason,
                Array.Empty<VbaRibbonPart>(), Array.Empty<VbaRibbonCallbackCheck>());
        }

        var parts = read.Parts.Select(p => VbaRibbonXmlAnalyzer.Analyze(p, limits.MaxPartBytes)).ToArray();
        var usable = parts.Where(p => p.IsUsable).ToArray();
        if (usable.Length == 0)
        {
            var reasons = string.Join("、", parts.Select(p => $"{p.Raw.PartName}: {(p.Problem ?? string.Empty).TrimEnd('。')}"));
            return new VbaRibbonSnapshot(
                name, workbookPath, now, read.FileModified, VbaRibbonReadStatus.PartsUnreadable,
                $"部品を読めません（{reasons}）",
                parts, Array.Empty<VbaRibbonCallbackCheck>());
        }

        var modules = await ReadMirrorModulesAsync(mirrorRoot, ct).ConfigureAwait(false);
        var references = usable.SelectMany(p => p.Nodes).SelectMany(n => n.Callbacks).ToArray();
        var checks = VbaRibbonCallbackMatcher.Check(references, modules);

        var unusable = parts.Where(p => !p.IsUsable).ToArray();
        var reason = unusable.Length == 0
            ? null
            : "一部の部品は読めませんでした（" + string.Join("、", unusable.Select(p => $"{p.Raw.PartName}: {(p.Problem ?? string.Empty).TrimEnd('。')}")) + "）";
        return new VbaRibbonSnapshot(
            name, workbookPath, now, read.FileModified, VbaRibbonReadStatus.Ok, reason, parts, checks);
    }

    /// <summary>
    /// ミラーの全モジュールの本文を読む。ミラーのフォルダ自体が無い・読めないときは null（照合できない）。
    /// 個々のファイルが読めないときは、そのファイルだけ飛ばす（照合は残りで行う。見つからないと出るのは「念のため確認」の側）。
    /// </summary>
    private static async Task<IReadOnlyList<VbaRibbonSourceModule>?> ReadMirrorModulesAsync(string mirrorRoot, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(mirrorRoot) || !Directory.Exists(mirrorRoot)) return null;

        var modules = new List<VbaRibbonSourceModule>();
        try
        {
            foreach (var kind in Enum.GetValues<VbaModuleKind>())
            {
                var folder = Path.Combine(mirrorRoot, VbaModuleKinds.FolderName(kind));
                if (!Directory.Exists(folder)) continue;

                var extension = VbaModuleKinds.Extension(kind);
                foreach (var file in Directory.EnumerateFiles(folder, "*" + extension, SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!string.Equals(Path.GetExtension(file), extension, StringComparison.OrdinalIgnoreCase)) continue;
                    if (new FileInfo(file).Length > MaxMirrorFileBytes) continue;

                    var text = await FileTextIO.ReadAsync(file, ct).ConfigureAwait(false);
                    if (!text.IsSuccess) continue;
                    modules.Add(new VbaRibbonSourceModule(Path.GetFileNameWithoutExtension(file), kind, text.Value.Text));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return modules;
    }
}
