using Graft.Features;

namespace Graft.Core.Vba;

/// <summary>
/// VBA プロジェクトの実効の許可拡張子（仕様書_VBA連携 4.1）。
/// <para>
/// 【なぜ必要か】 書き込み（<c>PathGuardOptions.AllowedExtensions</c>）・クイックオープン・エクスプローラ操作は
/// <c>Settings.Safety.AllowedExtensions</c> をそのまま使っており、プロジェクト別の上書き
/// （<c>ProjectOverrideResolver</c>）は配線されていない。ミラーのファイルは <c>.bas</c>／<c>.cls</c>／
/// <c>.frm</c> なので、既定値に足しても保存済みの設定（利用者が既に持っている settings.json）には届かず、
/// 適用が E202 で止まる。そこで<b>VBA プロジェクトに限り</b>、実効の許可拡張子にこの3つを自動で足す。
/// 全体の既定とユーザーの設定は変えない。
/// </para>
/// </summary>
public static class VbaExtensionPolicy
{
    /// <summary>VBA プロジェクトで追加する拡張子。</summary>
    public static readonly IReadOnlyList<string> VbaExtensions = new[] { ".bas", ".cls", ".frm" };

    /// <summary>
    /// 実効の許可拡張子を返す。<paramref name="project"/> が VBA プロジェクトでなければ、
    /// <paramref name="allowed"/> をそのまま（同じ並びで）返す。VBA プロジェクトなら、足りない分だけを
    /// 末尾に足す（既に入っている拡張子は大文字小文字を区別せず重複させない）。
    /// </summary>
    public static IReadOnlyList<string> Effective(IReadOnlyList<string> allowed, Project? project)
        => Effective(allowed, project?.Vba is not null);

    /// <summary><see cref="Effective(IReadOnlyList{string}, Project?)"/> の、プロジェクトの型に依存しない版。</summary>
    public static IReadOnlyList<string> Effective(IReadOnlyList<string> allowed, bool isVbaProject)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        if (!isVbaProject) return allowed;

        var result = new List<string>(allowed);
        foreach (var extension in VbaExtensions)
        {
            if (!result.Any(a => string.Equals(a, extension, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(extension);
            }
        }

        return result;
    }
}
