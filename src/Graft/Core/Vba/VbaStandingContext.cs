namespace Graft.Core.Vba;

/// <summary>
/// VBA プロジェクトの <c>StandingContext</c>（常設コンテキスト）の既定文（仕様書_VBA連携 7.1 の 5）。
/// AI に毎回渡る説明で、ミラーの形（Attribute 行を含まない、フォルダの意味）と、適用できない操作
/// （Documents/・Forms/ の新規作成、DELETE／RENAME、Shift_JIS で表せない文字）を先に伝えて、
/// 適用の段階で止まる出力を減らす。利用者が編集できる。
/// </summary>
public static class VbaStandingContext
{
    /// <summary>既定の文。</summary>
    public const string Default =
        "これは Excel VBA のプロジェクトです。各ファイルは VBE のモジュール本文そのもので、Attribute 行やエクスポート用のヘッダは含みません。\n" +
        "Modules/ は標準モジュール、Classes/ はクラスモジュール、Documents/ はシートと ThisWorkbook、Forms/ はユーザーフォームのコードです。\n" +
        "新しいモジュールは Modules/ または Classes/ に作ってください。Documents/ と Forms/ に新しいファイルを作ることはできません。\n" +
        "ファイルの削除と名前の変更（DELETE / RENAME）はできません。\n" +
        "Shift_JIS（CP932）で表現できない文字（絵文字、波ダッシュ「〜」、emダッシュ「—」など）は使わないでください。";
}
