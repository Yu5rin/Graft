namespace Graft.Features;

/// <summary><see cref="GitIntegration.GetChangedFilesAsync"/> の結果の区分。</summary>
public enum GitChangedFilesState
{
    /// <summary>変更ファイルの一覧を取得できた（0件のこともある）。</summary>
    Ready,

    /// <summary>git コマンドが見つからない（未インストール、PATH が通っていない等）。</summary>
    GitNotFound,

    /// <summary>git は使えるが、プロジェクトのフォルダが git リポジトリの中にない。</summary>
    NotARepository,

    /// <summary>git は使え、リポジトリでもあるが、状態の取得に失敗した（タイムアウト等）。</summary>
    Failed,
}

/// <summary>
/// 未コミットの変更があるファイルの一覧（コンテキスト収集の「gitの変更ファイルだけ」用）。
/// <see cref="Paths"/> は**プロジェクトのルートからの相対パス**（区切りは <c>/</c>）で、
/// git のリポジトリルートからの相対ではない（<see cref="GitChangedFilesParser"/> 参照）。
/// </summary>
public sealed record GitChangedFiles
{
    /// <summary>取得結果の区分。<see cref="GitChangedFilesState.Ready"/> 以外では <see cref="Paths"/> は空。</summary>
    public GitChangedFilesState State { get; init; }

    /// <summary>
    /// 変更・追加・未追跡・名前変更の新しい側のファイル（プロジェクト相対、重複なし）。
    /// 削除されたファイルとプロジェクトの外のファイルは含まない。
    /// </summary>
    public IReadOnlyList<string> Paths { get; init; } = Array.Empty<string>();

    /// <summary>削除されていて、もう存在しないために <see cref="Paths"/> から除いたファイルの数。</summary>
    public int DeletedCount { get; init; }

    /// <summary><see cref="GitChangedFilesState.Failed"/> のときの git の出力（原因の手掛かり）。</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// <c>git status --porcelain=v1 -z</c> の出力を、プロジェクト相対のパス一覧へ直す純ロジック。
///
/// 【なぜ -z（NUL 区切り）の出力を読むか】
/// 既定の porcelain 出力は、空白や引用符を含むパスを <c>"a b.txt"</c> のように二重引用符で
/// 囲み、名前変更は <c>old -> new</c> の 1 行で返す。これを文字列として切り出すと、
/// 「名前に矢印を含むファイル」や引用符の外し方を取り違える。-z では引用符も矢印も無く、
/// 名前変更は「新しい側 NUL 古い側 NUL」の 2 項目として返るので、取り違えようがない。
///
/// 【なぜ git のパスをそのまま使わず、接頭辞を外すか】
/// <c>git status</c> が返すパスは、カレントディレクトリではなく**リポジトリルート**からの相対。
/// Graft のプロジェクトのルートがリポジトリのサブフォルダ（例: モノレポの <c>app/</c>）のとき、
/// そのまま照合すると <c>app/src/a.cs</c> は走査結果の <c>src/a.cs</c> に一致せず、変更ファイルが
/// すべて「見つからない」になる。<c>git rev-parse --show-prefix</c>（プロジェクトルートの
/// リポジトリルートからの相対。ルートそのものなら空）を外し、外れるもの（リポジトリの別の場所の
/// 変更）はプロジェクトの外として捨てる。
/// </summary>
public static class GitChangedFilesParser
{
    /// <summary>
    /// 出力を解釈する。
    /// </summary>
    /// <param name="porcelainZOutput"><c>git status --porcelain=v1 -z</c> の出力。</param>
    /// <param name="repositoryPrefix">
    /// プロジェクトルートのリポジトリルートからの相対パス（<c>git rev-parse --show-prefix</c> の値。
    /// 例: <c>app/</c>）。リポジトリルートそのものなら空。区切りは <c>/</c>、末尾は <c>/</c> でも無くてもよい。
    /// </param>
    public static (IReadOnlyList<string> Paths, int DeletedCount) Parse(string porcelainZOutput, string repositoryPrefix)
    {
        ArgumentNullException.ThrowIfNull(porcelainZOutput);
        var prefix = NormalizePrefix(repositoryPrefix);

        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deleted = 0;

        var fields = porcelainZOutput.Split('\0');
        for (var i = 0; i < fields.Length; i++)
        {
            var field = fields[i];

            // 形式は "XY パス"（X・Y は 1 文字ずつ、3 文字目は空白）。標準エラーの警告文などが
            // 末尾に混じっても（呼び出し側は標準出力と標準エラーをまとめて受け取る）、
            // 状態の 2 文字が git の定める記号でなければ変更ファイルとして数えない。
            if (field.Length < 4 || field[2] != ' ' || !IsStatusChar(field[0]) || !IsStatusChar(field[1])) continue;

            var x = field[0];
            var y = field[1];
            var path = field[3..];

            // 名前変更・コピーは、直後の項目が「元のパス」。新しい側（この項目）だけを使い、
            // 元のパスは変更ファイルとして数えない（もう存在しない名前のため）。
            if (x is 'R' or 'C' || y is 'R' or 'C') i++;

            // 無視されているファイル（'!'）は --ignored を付けない限り出ないが、念のため除く。
            if (x == '!' && y == '!') continue;

            if (!TryMakeProjectRelative(path, prefix, out var relative)) continue;

            // 作業ツリーから消えているもの: 作業ツリー側が D、または索引側だけが D（git rm 済み）。
            if (y == 'D' || (x == 'D' && y == ' '))
            {
                deleted++;
                continue;
            }

            if (seen.Add(relative)) paths.Add(relative);
        }

        return (paths, deleted);
    }

    private static bool IsStatusChar(char c) => c is ' ' or 'M' or 'T' or 'A' or 'D' or 'R' or 'C' or 'U' or '?' or '!';

    private static string NormalizePrefix(string prefix)
    {
        var p = prefix.Trim().Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        p = p.Trim('/');
        return p.Length == 0 ? string.Empty : p + "/";
    }

    private static bool TryMakeProjectRelative(string repoRelativePath, string prefix, out string relative)
    {
        var path = repoRelativePath.Replace('\\', '/').TrimEnd('/');
        if (prefix.Length == 0)
        {
            relative = path;
            return path.Length > 0;
        }

        // Windows など大文字小文字を区別しないファイルシステムでは、git の表記と
        // rev-parse の接頭辞で大文字小文字が食い違い得るため、比較は区別しない。
        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && path.Length > prefix.Length)
        {
            relative = path[prefix.Length..];
            return true;
        }

        relative = string.Empty;
        return false;
    }
}
