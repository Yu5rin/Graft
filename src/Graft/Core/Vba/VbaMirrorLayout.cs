namespace Graft.Core.Vba;

/// <summary>ミラー内の1ファイルを (種別, コンポーネント名) に読み解いた結果。</summary>
/// <param name="Kind">モジュール種別。</param>
/// <param name="ComponentName">VBA のコンポーネント名（ファイル名から拡張子を除いたもの）。</param>
/// <param name="RelativePath">正規の相対パス（<c>Modules/Foo.bas</c> 形式。区切りは <c>/</c>、フォルダ名と拡張子は正規の大文字小文字）。</param>
public sealed record VbaMirrorPath(VbaModuleKind Kind, string ComponentName, string RelativePath);

/// <summary>
/// ミラーのパス ⇔ (種別, コンポーネント名) の相互変換（仕様書_VBA連携 4.2・6.1）。
/// <para>
/// 【ファイル名の大文字小文字を区別しない理由】 VBA のコンポーネント名は大文字小文字を区別せず、
/// Windows のファイルシステムも同様なので、<c>modules/foo.BAS</c> を <c>Modules/Foo.bas</c> と
/// 別物として扱うと、同じモジュールを二重に同期してしまう。解釈の結果は常に正規の綴りの
/// <see cref="VbaMirrorPath.RelativePath"/> に直し、同期状態のキーにはそれを使う。
/// </para>
/// </summary>
public static class VbaMirrorLayout
{
    /// <summary>種別とコンポーネント名から、ミラー内の正規の相対パス（<c>/</c> 区切り）を作る。</summary>
    public static string ToRelativePath(VbaModuleKind kind, string componentName)
        => VbaModuleKinds.FolderName(kind) + "/" + componentName + VbaModuleKinds.Extension(kind);

    /// <summary>
    /// ミラー内の相対パスを (種別, コンポーネント名) に読み解く。同期の対象外（失敗）になるのは次の場合で、
    /// <paramref name="unsupportedReason"/> に理由を日本語で返す（E810 の警告の詳細に使う）。
    /// ミラーの直下や未知のフォルダのファイル、入れ子のフォルダ（<c>Modules/sub/x.bas</c>）、
    /// フォルダと拡張子の不一致（<c>Modules/x.cls</c>）、ミラーの外を指すパス、
    /// Windows の予約名などミラーのファイルにできないコンポーネント名。
    /// </summary>
    public static bool TryParse(string relativePath, out VbaMirrorPath? result, out string? unsupportedReason)
    {
        result = null;
        unsupportedReason = null;

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            unsupportedReason = "パスが空です。";
            return false;
        }

        // Windows の \ 区切りも受け付ける。先頭の ./ は無視する。
        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != ".").ToArray();

        if (relativePath.StartsWith('/') || relativePath.StartsWith('\\')
            || (segments.Length > 0 && segments[0].Length >= 2 && segments[0][1] == ':')
            || segments.Contains(".."))
        {
            unsupportedReason = "ミラーの外を指すパスです。";
            return false;
        }

        if (segments.Length < 2)
        {
            unsupportedReason = "ミラーの直下です（Modules/・Classes/・Forms/・Documents/ の中に置いてください）。";
            return false;
        }

        if (segments.Length > 2)
        {
            unsupportedReason = "入れ子のフォルダは同期できません（フォルダの直下に置いてください）。";
            return false;
        }

        var folder = segments[0];
        var fileName = segments[1];
        VbaModuleKind? matchedKind = null;
        foreach (var kind in Enum.GetValues<VbaModuleKind>())
        {
            if (string.Equals(folder, VbaModuleKinds.FolderName(kind), StringComparison.OrdinalIgnoreCase))
            {
                matchedKind = kind;
                break;
            }
        }

        if (matchedKind is not { } folderKind)
        {
            unsupportedReason = $"「{folder}」は同期のフォルダではありません（Modules/・Classes/・Forms/・Documents/ のいずれかです）。";
            return false;
        }

        var extension = Path.GetExtension(fileName);
        var expected = VbaModuleKinds.Extension(folderKind);
        if (!string.Equals(extension, expected, StringComparison.OrdinalIgnoreCase))
        {
            unsupportedReason = $"{VbaModuleKinds.FolderName(folderKind)}/ のファイルの拡張子は {expected} です。";
            return false;
        }

        var name = fileName[..^extension.Length];
        if (!VbaNameRules.CanMirror(name))
        {
            unsupportedReason = VbaNameRules.IsWindowsReservedName(name)
                ? $"「{name}」は Windows の予約名のため、ミラーのファイルにできません。"
                : $"「{name}」はコンポーネント名として使えません。";
            return false;
        }

        result = new VbaMirrorPath(folderKind, name, ToRelativePath(folderKind, name));
        return true;
    }

    /// <summary>
    /// 相対パスの一覧から、同期の対象外のファイルを E810（警告）として列挙する。適用そのものは止めない。
    /// </summary>
    public static IReadOnlyList<GraftIssue> CollectUnsupported(IEnumerable<string> relativePaths)
    {
        var issues = new List<GraftIssue>();
        foreach (var path in relativePaths)
        {
            if (TryParse(path, out _, out var reason)) continue;
            issues.Add(GraftIssue.Of(ErrorCode.E810, detail: reason, path: path, severity: Severity.Warning));
        }

        return issues;
    }
}
