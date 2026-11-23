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
/// <para>
/// 【<c>.xml</c> の許可を、リボンの同期が有効なときだけにした理由（不具合の修正。v1.3）】 初めは、全 VBA プロジェクトの実効の許可に <c>.xml</c> を
/// 足していた。すると、利用者が許可リストから <c>.xml</c> を外していても、また設定「リボンをミラーのファイルとして扱う」をオフにしていても、
/// VBA プロジェクトでは <c>CustomUI/</c> の外の <c>.xml</c> まで書けてしまう（利用者の意図した制限が黙って緩む）。そこで、リボンの同期が有効な
/// ときだけ <c>.xml</c> を足す。
/// <b>これ以上は絞れない（<c>CustomUI/</c> の下だけにはできない）</b>: <c>PathGuard</c> の許可は拡張子（と、拡張子の無い名前）の一覧だけで、
/// パスごとの許可を持たず、<c>PathGuard</c> は封印されていて（<c>sealed</c>）本体は変更できない。したがって「同期が有効なら、そのプロジェクトの
/// 実効の許可に <c>.xml</c> を足す」が、いまの仕組みで取れる最小の範囲になる。ミラーのフォルダは Graft が管理する場所で、リボンのファイル以外の
/// <c>.xml</c> を Excel へ反映することはない（<c>CustomUI/customUI14.xml</c>・<c>customUI.xml</c> の 2 つだけが同期の対象）ので、実害は小さい。
/// </para>
/// </summary>
public static class VbaExtensionPolicy
{
    /// <summary>VBA プロジェクトで追加する拡張子（モジュールのファイル）。</summary>
    public static readonly IReadOnlyList<string> VbaExtensions = new[] { ".bas", ".cls", ".frm" };

    /// <summary>
    /// リボンの同期が有効な VBA プロジェクトで追加する拡張子。<c>.xml</c> はリボン（<c>CustomUI/customUI14.xml</c>・<c>CustomUI/customUI.xml</c>。
    /// 仕様書_ブックの情報と依頼の観点 16.3）のため。<c>.xml</c> は既定の許可リストに入っているが、利用者が外していても、リボンの同期が有効な
    /// VBA プロジェクトでは、取り込んだリボンのファイルへ AI のパッチを当てられるようにする（外したままだと、取込でミラーに現れたファイルへの
    /// 適用だけが E202 で止まる）。並びの末尾に足すので、既存の並びは変わらない。
    /// </summary>
    public static readonly IReadOnlyList<string> RibbonExtensions = new[] { ".xml" };

    /// <summary>
    /// 実効の許可拡張子を返す。<paramref name="project"/> が VBA プロジェクトでなければ、
    /// <paramref name="allowed"/> をそのまま（同じ並びで）返す。VBA プロジェクトなら、足りない分だけを
    /// 末尾に足す（既に入っている拡張子は大文字小文字を区別せず重複させない）。
    /// </summary>
    /// <param name="allowed">利用者の許可リスト。</param>
    /// <param name="project">対象のプロジェクト。</param>
    /// <param name="ribbonSyncEnabled">
    /// リボンの同期が有効か（設定 <c>Vba.RibbonEditEnabled</c>）。true のときだけ <c>.xml</c> を足す。省略（false）は、足さない側（安全側）。
    /// </param>
    public static IReadOnlyList<string> Effective(IReadOnlyList<string> allowed, Project? project, bool ribbonSyncEnabled = false)
        => Effective(allowed, project?.Vba is not null, ribbonSyncEnabled);

    /// <summary><see cref="Effective(IReadOnlyList{string}, Project?, bool)"/> の、プロジェクトの型に依存しない版。</summary>
    public static IReadOnlyList<string> Effective(IReadOnlyList<string> allowed, bool isVbaProject, bool ribbonSyncEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        if (!isVbaProject) return allowed;

        var result = new List<string>(allowed);
        foreach (var extension in ribbonSyncEnabled ? VbaExtensions.Concat(RibbonExtensions) : VbaExtensions)
        {
            if (!result.Any(a => string.Equals(a, extension, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(extension);
            }
        }

        return result;
    }
}
