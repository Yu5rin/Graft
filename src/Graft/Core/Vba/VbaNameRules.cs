namespace Graft.Core.Vba;

/// <summary>新規モジュール名の検査結果。</summary>
public readonly record struct VbaNameCheck(bool IsValid, string? Reason)
{
    public static VbaNameCheck Ok() => new(true, null);

    public static VbaNameCheck Fail(string reason) => new(false, reason);
}

/// <summary>
/// VBA コンポーネント名の規則（仕様書_VBA連携 6.2）。
/// <para>
/// 検査は2種類に分ける。<b>新規作成</b>は厳しく（31 文字以内、先頭は英字、英数字とアンダースコアのみ、
/// Windows の予約名は不可）、<b>既存</b>の名前は緩く（ミラーのファイルとして作れさえすればよい）。
/// 日本語名は VBE が受け付ける場合もあるが環境によって挙動が違うので、新規作成のときだけ拒否し、
/// 既存の日本語名のモジュールは読み書きできるようにする。
/// </para>
/// </summary>
public static class VbaNameRules
{
    /// <summary>コンポーネント名の最大長。VBE の上限（31 文字）に合わせる。</summary>
    public const int MaxNameLength = 31;

    private static readonly HashSet<string> ReservedNames = BuildReservedNames();

    // Windows のファイル名に使えない記号。Linux の Path.GetInvalidFileNameChars は '/' と NUL しか返さないため、
    // OS によって結果が変わらないよう固定の一覧を使う（テストが Linux でも Windows でも同じ結果になる）。
    private static readonly char[] InvalidFileNameChars =
    {
        '<', '>', ':', '"', '/', '\\', '|', '?', '*', '\0',
    };

    private static HashSet<string> BuildReservedNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CON", "PRN", "AUX", "NUL" };
        for (var i = 1; i <= 9; i++)
        {
            set.Add("COM" + i);
            set.Add("LPT" + i);
        }

        return set;
    }

    /// <summary>
    /// Windows の予約名（CON・PRN・AUX・NUL・COM1〜COM9・LPT1〜LPT9。大文字小文字を区別しない）か。
    /// これらの名前のファイルは作れないため、ミラーに写せない（Pull ではスキップして警告する）。
    /// </summary>
    public static bool IsWindowsReservedName(string name) => ReservedNames.Contains(name);

    /// <summary>
    /// 既存のコンポーネント名として、ミラーのファイル名にできるか。空でない、ファイル名に使えない文字や
    /// ドット（拡張子との区切りと紛れる。VBA の名前にも使えない）や制御文字を含まない、予約名でない、
    /// 前後に空白がない。新規作成の規則（<see cref="ValidateNewName"/>）より緩く、日本語名や
    /// 31 文字を超える名前も通す。
    /// </summary>
    public static bool CanMirror(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (!string.Equals(name, name.Trim(), StringComparison.Ordinal)) return false;
        if (name.IndexOf('.') >= 0) return false;
        if (name.IndexOfAny(InvalidFileNameChars) >= 0) return false;
        foreach (var c in name)
        {
            if (char.IsControl(c)) return false;
        }

        return !IsWindowsReservedName(name);
    }

    /// <summary>
    /// 新規作成するモジュールの名前を検査する。31 文字以内、先頭は英字（A-Z, a-z）、
    /// 使える文字は英数字（ASCII）とアンダースコア、Windows の予約名は不可。
    /// 予約語（<c>Module</c>・<c>Class</c> など）との衝突は検査しない（VBE がエラーを返し、E808 になる）。
    /// </summary>
    public static VbaNameCheck ValidateNewName(string name)
    {
        if (string.IsNullOrEmpty(name)) return VbaNameCheck.Fail("名前が空です。");
        if (name.Length > MaxNameLength)
        {
            return VbaNameCheck.Fail($"名前は {MaxNameLength} 文字以内にしてください（{name.Length} 文字）。");
        }

        if (!IsAsciiLetter(name[0])) return VbaNameCheck.Fail("名前の先頭は英字（A〜Z）にしてください。");
        foreach (var c in name)
        {
            if (!IsAsciiLetter(c) && !(c is >= '0' and <= '9') && c != '_')
            {
                return VbaNameCheck.Fail($"名前に使える文字は英数字とアンダースコアだけです（「{c}」は使えません）。");
            }
        }

        if (IsWindowsReservedName(name))
        {
            return VbaNameCheck.Fail($"「{name}」は Windows の予約名のため、ファイルとして保存できません。");
        }

        return VbaNameCheck.Ok();
    }

    private static bool IsAsciiLetter(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');
}
