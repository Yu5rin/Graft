using Graft.Features;

namespace Graft.Core.Vba;

/// <summary>
/// VBA プロジェクトの実効の許可拡張子（仕様書_VBA連携 4.1）。
/// <para>
/// 【なぜ必要か】 書き込み（<c>PathGuardOptions.AllowedExtensions</c>）・クイックオープン・エクスプローラ操作は
/// <c>Settings.Safety.AllowedExtensions</c> をそのまま使っており、プロジェクト別の上書き
/// （<c>ProjectOverrideResolver</c>）は配線されていない。ミラーのファイルは <c>.bas</c>／<c>.cls</c>／
/// <c>.frm</c>（とリボンの <c>.xml</c>）なので、既定値に足しても保存済みの設定（利用者が既に持っている settings.json）には届かず、
/// 適用が E202 で止まる。そこで<b>VBA プロジェクトに限り</b>、実効の許可拡張子にこれらを自動で足す。
/// 全体の既定とユーザーの設定は変えない。
/// </para>
/// </summary>
public static class VbaExtensionPolicy
{
    /// <summary>
    /// VBA プロジェクトで追加する拡張子。<c>.xml</c> はリボン（<c>CustomUI/customUI14.xml</c>・<c>CustomUI/customUI.xml</c>。
    /// 仕様書_ブックの情報と依頼の観点 16.3）のため。<c>.xml</c> は既定の許可リストに入っているが、利用者が外していても、VBA
    /// プロジェクトでは取り込んだリボンのファイルへ AI のパッチを当てられるようにする（外したままだと、取込でミラーに現れた
    /// ファイルへの適用だけが E202 で止まる）。並びの末尾に足すので、既存の 3 つの並びは変わらない。
    /// </summary>
    public static readonly IReadOnlyList<string> VbaExtensions = new[] { ".bas", ".cls", ".frm", ".xml" };

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
