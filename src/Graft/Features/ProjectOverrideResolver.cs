using Graft.Infra;

namespace Graft.Features;

/// <summary>
/// プロジェクト単位の <see cref="ProjectOverrides"/>（仕様書3.1）を全体設定
/// （<see cref="Settings"/>）へ適用し、そのプロジェクトに対して有効な設定を組み立てる。
/// </summary>
public static class ProjectOverrideResolver
{
    /// <summary>
    /// overrides.allowedExtensions の "+" / "-" 接頭辞を解決し、newFileEncoding の
    /// 上書きを反映した Settings を返す。excludes については下記コメントを参照。
    /// </summary>
    public static Settings Apply(Settings baseSettings, Project project)
    {
        ArgumentNullException.ThrowIfNull(baseSettings);
        ArgumentNullException.ThrowIfNull(project);

        var overrides = project.Overrides;
        var resolved = baseSettings with
        {
            Safety = baseSettings.Safety with
            {
                AllowedExtensions = ResolveAllowedExtensions(baseSettings.Safety.AllowedExtensions, overrides.AllowedExtensions),
            },
        };

        if (!string.IsNullOrWhiteSpace(overrides.NewFileEncoding))
        {
            resolved = resolved with
            {
                Encoding = resolved.Encoding with { NewFileEncoding = overrides.NewFileEncoding },
            };
        }

        // 仕様書3.1は「excludes は Settings.Context 側の除外パターンへ追加する」としているが、
        // Infra/Settings.cs の ContextSettings（本担当の編集対象外）には除外パターンを保持する
        // フィールドが存在しない。projects.json 側の ProjectOverrides.Excludes は保持しているため
        // 値自体は失われないが、ここで Settings へ反映することはできない。
        // Infra担当へ ContextSettings への除外パターン用フィールド追加を確認事項として報告する。

        return resolved;
    }

    /// <summary>
    /// "+" 接頭辞は全体設定への追加、"-" は除外を意味する。接頭辞のないエントリが
    /// 1件でも含まれる場合は、そのプロジェクトが拡張子一覧を丸ごと置き換える意図と
    /// みなし、全体設定は無視してそのエントリ群をそのまま採用する（+/- との混在は
    /// 想定しない仕様のため、置き換え指定を優先する）。
    /// </summary>
    private static List<string> ResolveAllowedExtensions(
        IReadOnlyList<string> baseExtensions, IReadOnlyList<string> overrideEntries)
    {
        if (overrideEntries.Count == 0)
        {
            return baseExtensions.ToList();
        }

        var plainEntries = overrideEntries.Where(e => !e.StartsWith('+') && !e.StartsWith('-')).ToList();
        if (plainEntries.Count > 0)
        {
            return plainEntries.Select(NormalizeExtension).ToList();
        }

        var result = new List<string>(baseExtensions);
        foreach (var entry in overrideEntries)
        {
            ApplyExtensionEntry(result, entry);
        }
        return result;
    }

    private static void ApplyExtensionEntry(List<string> result, string entry)
    {
        var ext = NormalizeExtension(entry[1..]);
        if (entry.StartsWith('+'))
        {
            if (!result.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(ext);
            }
        }
        else if (entry.StartsWith('-'))
        {
            result.RemoveAll(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static string NormalizeExtension(string ext)
    {
        var trimmed = ext.Trim();
        return trimmed.StartsWith('.') ? trimmed : $".{trimmed}";
    }
}
