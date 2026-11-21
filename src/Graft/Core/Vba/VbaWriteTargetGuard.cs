namespace Graft.Core.Vba;

/// <summary>
/// 書き込む直前に「引いたコンポーネントが、書こうとしているものか」を確かめる純ロジック（不具合 1、および
/// <c>VBComponents.Item(添字)</c> の取り違えの防止）。COM に触れないので Linux でテストできる。
/// <para>
/// 【なぜ要るか】
/// </para>
/// <list type="bullet">
/// <item>
/// <b>種別</b>: コンポーネント名は種別をまたいで一意なので、ブリッジが名前だけで引くと、
/// <c>Classes/ThisWorkbook.cls</c>（クラス）の書き込みが Document の <c>ThisWorkbook</c> を引き当てて、
/// 本文を黙って置き換えてしまう。計画（<see cref="VbaSyncPlanner"/>）の段階でも止めるが、ブリッジが最後の砦として、
/// 引いたコンポーネントの <c>Type</c> が書こうとしている種別と違えば書かない。
/// </item>
/// <item>
/// <b>名前</b>: 名前→添字の表を作ってから書き込むまでの間に、<c>VBComponents.Add／Remove</c> や利用者の VBE 操作で
/// 並びが変わると、添字が別のモジュールを指す。取得したあとで <c>Name</c> を照合し、違えば書かない。
/// </item>
/// </list>
/// </summary>
public static class VbaWriteTargetGuard
{
    /// <summary>
    /// 問題があれば E808 の問題を返す。なければ null。
    /// </summary>
    /// <param name="requestedName">書き込もうとしているコンポーネント名。</param>
    /// <param name="requestedKind">書き込もうとしている種別。</param>
    /// <param name="actualName">引き当てたコンポーネントの名前。</param>
    /// <param name="actualKind">引き当てたコンポーネントの種別（未対応の <c>Type</c> なら null）。</param>
    public static GraftIssue? Check(string requestedName, VbaModuleKind requestedKind, string actualName, VbaModuleKind? actualKind)
    {
        ArgumentNullException.ThrowIfNull(requestedName);
        ArgumentNullException.ThrowIfNull(actualName);

        if (!string.Equals(requestedName, actualName, StringComparison.OrdinalIgnoreCase))
        {
            return GraftIssue.Of(
                ErrorCode.E808,
                detail: $"{requestedName}: 取得したコンポーネントの名前が「{actualName}」で一致しなかったため、書き込みませんでした" +
                        "（VBE でモジュールの並びが変わった可能性があります）。もう一度反映してください。");
        }

        if (actualKind != requestedKind)
        {
            var actual = actualKind is { } known ? VbaModuleKinds.Label(known) : "未対応の種別のコンポーネント";
            var hint = actualKind is { } k ? $" {VbaMirrorLayout.ToRelativePath(k, actualName)} に書いてください。" : string.Empty;
            return GraftIssue.Of(
                ErrorCode.E808,
                detail: $"{requestedName}: Excel 側の同じ名前のコンポーネントは{actual}で、{VbaModuleKinds.Label(requestedKind)}ではありません。" +
                        $"既存の本文を上書きしてしまうため、書きませんでした。{hint}");
        }

        return null;
    }
}
