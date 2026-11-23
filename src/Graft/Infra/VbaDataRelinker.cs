using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;

namespace Graft.Infra;

/// <summary>
/// データ保存先の移行後に、VBA プロジェクトの <c>Root</c> と <c>Id</c> を新しい保存先へ付け替える
/// （仕様書_VBA連携 4.4）。
/// <para>
/// 【なぜ要るか】 ミラーは既定でデータ保存先の <c>vba-mirrors</c> 配下に置かれる。保存先の移行で
/// <c>projects.json</c> ごとコピーされるが、中の <c>Root</c> は旧保存先の絶対パスのまま。放っておくと、
/// 旧保存先の掃除（次回起動時）でミラーごと消えた場所を指す「未接続のプロジェクト」になる。Id は Root の
/// ハッシュから導く（<see cref="ProjectStore.CreateId"/>）ので、Root を変えれば Id も変わる。Id をキーに
/// 持つほかのデータも、同じ変換をかけないと取り残される。
/// </para>
/// <para>
/// 【Id を持つ場所の洗い出し（実装時に grep で確認した結果）】
/// <list type="bullet">
/// <item><c>projects.json</c> の <c>id</c>（と <c>root</c>）</item>
/// <item><c>back/&lt;Id&gt;/</c> フォルダ</item>
/// <item><c>vba-sync/&lt;Id&gt;.json</c> と <c>vba-sync/&lt;Id&gt;.rescue/</c>・<c>vba-sync/&lt;Id&gt;.workbooks/</c>
/// （リボンの書き換えの前のブックの控え。仕様書_ブックの情報と依頼の観点 16.11）</item>
/// <item><c>queue.json</c> の各項目の <c>projectId</c></item>
/// <item><c>layout.json</c> の <c>projectPaneWidths</c> のキー</item>
/// <item><c>back/&lt;Id&gt;/*/manifest.json</c> の <c>projectId</c>。履歴の読み書きでは照合に使われない
/// 記録用の値で、場所の変更（<see cref="ProjectStore.RelocateAsync"/>）でも書き換えていないため、ここでも
/// 数千件のファイルを書き換えることはしない。</item>
/// </list>
/// そのほか <c>templates.json</c>・<c>settings.json</c> には Id を持つ項目が無いことを確かめた。
/// </para>
/// <para>
/// 利用者が自分で選んだ場所（旧 <c>vba-mirrors</c> の外）のミラーは、移行しても動かないので触らない。
/// </para>
/// </summary>
public static class VbaDataRelinker
{
    private static readonly JsonSerializerOptions WriteOptions = JsonFileStore.DefaultOptions;

    /// <summary>
    /// 付け替えを行う。<paramref name="target"/> のファイルだけを書き換え、<paramref name="source"/> には触れない。
    /// </summary>
    /// <param name="source">旧保存先。</param>
    /// <param name="target">新保存先（コピー済み）。</param>
    /// <param name="replaceExisting">
    /// 付け替え先の <c>back/&lt;新Id&gt;</c> などが既に在るときの扱い。通常の移行では false（在れば失敗にして
    /// 安全側へ倒す）。起動時の取り込み直し（旧保存先のほうが新しいデータを持つ）では true（旧保存先の
    /// 内容で置き換える）。
    /// </param>
    public static GraftResult<int> Relink(AppPaths source, AppPaths target, bool replaceExisting)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        try
        {
            var map = RewriteProjects(source, target);
            if (map.Count == 0) return GraftResult<int>.Ok(0);

            var sync = new VbaSyncStateStore(target);
            foreach (var (oldId, newId) in map)
            {
                MoveDirectory(target.GetProjectBackupDirectory(oldId), target.GetProjectBackupDirectory(newId), replaceExisting);

                // 同期状態は旧Idのファイルを新Idへ移す。移す先が既に在る場合は、replaceExisting のときだけ
                // 上書きする（VbaSyncStateStore.Move は上書きで移す）。通常の移行では在りえないが、安全側で失敗にする。
                var newFile = target.GetVbaSyncFilePath(newId);
                if (!replaceExisting && File.Exists(newFile) && File.Exists(target.GetVbaSyncFilePath(oldId)))
                {
                    return GraftResult<int>.Fail(ErrorCode.E407, $"付け替え先の同期状態ファイルが既に存在します: {newFile}");
                }

                if (replaceExisting)
                {
                    DeleteIfExists(target.GetVbaRescueDirectory(newId));
                    // リボンの書き換えの前に残したブックの丸ごとの控え（仕様書_ブックの情報と依頼の観点 16.11）も、.rescue と同じ扱い。
                    // 旧保存先の内容で置き換えるときに新 Id 側の古いフォルダを残すと、Move が「移す先がある」として移さず、旧 Id の
                    // 控えが置き去りになる。
                    DeleteIfExists(target.GetVbaWorkbookBackupDirectory(newId));
                }

                sync.Move(oldId, newId);
            }

            RewriteQueue(target.QueueFilePath, map);
            RewriteLayout(target.WindowLayoutFilePath, map);
            return GraftResult<int>.Ok(map.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return GraftResult<int>.Fail(ErrorCode.E407, "VBA ミラーの付け替えに失敗しました。" + ExceptionMessages.Describe(ex));
        }
    }

    /// <summary>projects.json の対象プロジェクトの root と id を書き換え、旧Id→新Id の対応を返す。</summary>
    private static Dictionary<string, string> RewriteProjects(AppPaths source, AppPaths target)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        // projects.json は { "projects": [ ... ] } の形（ProjectCatalog）。素の配列の形も念のため受け付ける。
        var node = ReadJson(target.ProjectsFilePath);
        JsonArray? array = node as JsonArray;
        if (array is null && node is JsonObject catalog && TryGet(catalog, "projects", out var list))
        {
            array = list as JsonArray;
        }

        if (array is null || node is null) return map;

        var changed = false;
        foreach (var item in array)
        {
            if (item is not JsonObject project) continue;
            if (!TryGet(project, "vba", out var vba) || vba is null) continue;
            if (!TryGet(project, "root", out var rootNode) || rootNode is not JsonValue rootValue
                || !rootValue.TryGetValue<string>(out var root)) continue;

            var newRoot = VbaMirrorFolderName.RelocateRoot(root, source.VbaMirrorsDirectory, target.VbaMirrorsDirectory);
            if (newRoot is null) continue; // 利用者が選んだ場所のミラー。動かさない。

            var newId = ProjectStore.CreateId(newRoot);
            var oldId = TryGet(project, "id", out var idNode) && idNode is JsonValue idValue
                        && idValue.TryGetValue<string>(out var id) ? id : string.Empty;

            SetKey(project, "root", JsonValue.Create(newRoot));
            changed = true;
            if (oldId.Length > 0 && oldId != newId)
            {
                SetKey(project, "id", JsonValue.Create(newId));
                map[oldId] = newId;
            }
        }

        if (changed) WriteJson(target.ProjectsFilePath, node);
        return map;
    }

    /// <summary>queue.json の全ての projectId を付け替える（構造に依存しないよう、全体を再帰で走査する）。</summary>
    private static void RewriteQueue(string path, IReadOnlyDictionary<string, string> map)
    {
        var node = ReadJson(path);
        if (node is null) return;
        if (ReplaceProjectIds(node, map)) WriteJson(path, node);
    }

    private static bool ReplaceProjectIds(JsonNode? node, IReadOnlyDictionary<string, string> map)
    {
        var changed = false;
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    var child = obj[key];
                    if (string.Equals(key, "projectId", StringComparison.OrdinalIgnoreCase)
                        && child is JsonValue value && value.TryGetValue<string>(out var id)
                        && map.TryGetValue(id, out var replacement))
                    {
                        obj[key] = JsonValue.Create(replacement);
                        changed = true;
                    }
                    else if (ReplaceProjectIds(child, map))
                    {
                        changed = true;
                    }
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    if (ReplaceProjectIds(child, map)) changed = true;
                }

                break;
        }

        return changed;
    }

    /// <summary>layout.json の projectPaneWidths のキー（プロジェクトId）を付け替える。</summary>
    private static void RewriteLayout(string path, IReadOnlyDictionary<string, string> map)
    {
        if (ReadJson(path) is not JsonObject root) return;
        if (!TryGet(root, "projectPaneWidths", out var widthsNode) || widthsNode is not JsonObject widths) return;

        var changed = false;
        foreach (var (oldId, newId) in map)
        {
            var key = widths.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, oldId, StringComparison.Ordinal));
            if (key is null) continue;

            var value = widths[key];
            widths.Remove(key);
            widths[newId] = value;
            changed = true;
        }

        if (changed) WriteJson(path, root);
    }

    private static void MoveDirectory(string from, string to, bool replaceExisting)
    {
        if (!Directory.Exists(from)) return;
        if (Directory.Exists(to))
        {
            if (!replaceExisting) throw new IOException($"付け替え先のフォルダが既に存在します: {to}");
            Directory.Delete(to, recursive: true);
        }

        Directory.Move(from, to);
    }

    private static void DeleteIfExists(string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private static JsonNode? ReadJson(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            // 壊れたファイルは、通常の起動時の回復（JsonFileStore）に任せる。ここでは触れない。
            return null;
        }
    }

    /// <summary>同じフォルダ内の一時ファイルへ書いてから置き換える（書きかけを残さない）。</summary>
    private static void WriteJson(string path, JsonNode node)
    {
        var temp = path + ".relink.tmp";
        File.WriteAllText(temp, node.ToJsonString(WriteOptions));
        File.Move(temp, path, overwrite: true);
    }

    // JSON のキーは camelCase で保存されるが、手編集などに備えて大文字小文字は区別せず探す。
    private static bool TryGet(JsonObject obj, string name, out JsonNode? value)
    {
        foreach (var (key, child) in obj)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = child;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static void SetKey(JsonObject obj, string name, JsonNode? value)
    {
        var existing = obj.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        obj[existing ?? name] = value;
    }
}
