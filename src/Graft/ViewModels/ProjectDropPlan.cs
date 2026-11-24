using System.IO;
using Graft.Core.Vba;

namespace Graft.ViewModels;

/// <summary>
/// プロジェクト一覧へ落とされたものの振り分け結果（フォルダ／通常ファイルの親フォルダ／VBA の対象ブック／.xlsx）。
/// <para>
/// 【純粋な関数に切り出した理由】 Avalonia のドラッグ＆ドロップの実イベント（<c>DragEventArgs</c>・<c>IDataObject</c>）は
/// テストから安定して合成しにくい（<c>ProjectPane.ResolveDropTarget</c> と同じ事情）。振り分けだけを、パスの一覧から
/// 結果を返す <c>public static</c> にして、ファイルシステム（<see cref="Directory.Exists(string?)"/>）以外に依存しない形で検証する
/// （<c>ProjectPaneDropTargetTests</c>・<c>ProjectDropPlanTests</c>）。
/// </para>
/// <para>
/// 【ブックを親フォルダ登録にしない理由（1.3.1）】 以前は、ファイルが落とされたら一律にその親フォルダを登録していた。
/// .xlsm・.xlam などを落とす利用者が欲しいのは、置いてある場所のフォルダではなく、そのブックの VBA プロジェクトである。
/// ただし VBA 連携が使えない環境（Linux・設定オフ）では従来どおり親フォルダにする（今の挙動を壊さない）。
/// </para>
/// </summary>
/// <param name="Folders">登録するフォルダ（フォルダそのもの、または通常ファイルの親フォルダ。落とされた順）。</param>
/// <param name="Workbooks">VBA のプロジェクトにできるブック（.xlsm／.xlsb／.xlam／.xls。落とされた順）。VBA 連携が使えるときだけ入る。</param>
/// <param name="NoMacroWorkbooks">マクロを保存できない形式のブック（.xlsx）。VBA 連携が使えるときだけ入る。親フォルダ登録もしない。</param>
public sealed record ProjectDropPlan(
    IReadOnlyList<string> Folders,
    IReadOnlyList<string> Workbooks,
    IReadOnlyList<string> NoMacroWorkbooks)
{
    /// <summary>
    /// 落とされたローカルパスを振り分ける。<paramref name="vbaAvailable"/> が false なら、ブックも通常ファイルとして
    /// 親フォルダへ回す（従来の挙動）。null・空のパスは読み飛ばす。
    /// </summary>
    public static ProjectDropPlan Create(IEnumerable<string?> localPaths, bool vbaAvailable)
    {
        ArgumentNullException.ThrowIfNull(localPaths);
        var folders = new List<string>();
        var workbooks = new List<string>();
        var noMacro = new List<string>();

        foreach (var path in localPaths)
        {
            if (string.IsNullOrEmpty(path)) continue;

            // フォルダは、名前がブックの拡張子で終わっていても、フォルダとして登録する（拡張子で判定する前に実在で見る）。
            if (vbaAvailable && !Directory.Exists(path))
            {
                if (VbaRotNames.IsVbaWorkbookFile(path))
                {
                    workbooks.Add(path);
                    continue;
                }

                if (VbaRotNames.IsNoMacroWorkbookFile(path))
                {
                    noMacro.Add(path);
                    continue;
                }
            }

            var folder = ResolveFolder(path);
            if (!string.IsNullOrEmpty(folder)) folders.Add(folder);
        }

        return new ProjectDropPlan(folders, workbooks, noMacro);
    }

    /// <summary>
    /// 1件のローカルパスから登録すべきフォルダを決める（フォルダはそのまま、ファイルはその親フォルダ。取れなければ null）。
    /// <c>ProjectPane.ResolveDropTarget</c> の実体。
    /// </summary>
    public static string? ResolveFolder(string? localPath)
    {
        if (string.IsNullOrEmpty(localPath)) return null;
        return Directory.Exists(localPath) ? localPath : Path.GetDirectoryName(localPath);
    }
}
