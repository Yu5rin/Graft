using Avalonia;
using Avalonia.Media;

namespace Graft.Themes;

/// <summary>
/// UIフォント・本文フォント・等幅（コード）フォントの選択を一元管理する（検討書「フォント設定」）。
/// <see cref="ThemeManager"/>と同じ設計思想（<see cref="Application.Resources"/>を直接
/// 書き換えて即時反映、複数<see cref="Application"/>インスタンス＝headlessテストにも対応）だが、
/// 差し替え方はThemeManagerとは異なる。
///
/// 【なぜMergedDictionariesの差し替えではなく、キーを直接上書きするか】
/// UiFontFamily/BodyTextFontFamily/CodeFontFamilyはThemes/Tokens.axaml側に定義済みの単一
/// キーであり、ThemeManagerのようにテーマの数だけファイルを持たせる構成ではない（フォントの
/// 選択肢はOSにインストール済みのフォント全部であり、あらかじめファイルを用意できないため）。
/// Avaloniaのリソース解決は「そのResourceDictionary自身が直接持つキー」を
/// MergedDictionaries側より優先して見る（WPF由来の一般的な規則）ため、
/// <see cref="Application.Resources"/>（Tokens.axamlをマージ済みの器）へ直接キーを
/// 追加するだけでTokens.axaml側の既定値を上書きできる。既定（未設定）に戻す場合は
/// キーそのものを削除し、Tokens.axamlの既定値（プラットフォームのフォールバック列）へ戻す。
///
/// 【UIフォント・本文フォント・等幅フォントがどこへ効くか（v1.0.21で本文を分離）】
/// UiFontFamilyはFontFamilyが継承プロパティであることを利用して、ShellWindow等の
/// ルートWindowで一度指定するだけで大半のUI文字（設定画面・サイドバー・メニュー・ボタン等、
/// 明示的にFontFamilyを指定していない箇所すべて）へ継承で伝播する
/// （ShellWindow.axaml等のFontFamily="{DynamicResource UiFontFamily}"参照）。
/// BodyTextFontFamilyは取扱説明書（ManualWindow.ManualContentPanel）・Markdownプレビュー
/// （MarkdownPreviewView）の本文を置くコンテナだけがFontFamily="{DynamicResource
/// BodyTextFontFamily}"を指定しており、そこから配下の見出し・段落・箇条書き・表などの地の文へ
/// 継承で伝播する。同じウィンドウ内のボタン・検索欄などのUI部品はこのコンテナの外（同じ
/// ウィンドウの兄弟要素）にあるため、この継承の影響を受けずUiFontFamilyのままになる。
/// コードブロック・インラインコード（Views/ManualMarkdownRenderer.csのBuildCodeBlock・
/// BuildCodeRun）はBodyTextFontFamilyより内側にありながら、自身にCodeFontFamilyを明示指定
/// して継承を上書きしているため、本文フォントを変えても等幅のまま保たれる（「継承させておいて、
/// 必要な箇所だけ明示指定で上書きする」構造がここに既に成立している）。
/// CodeFontFamilyはAvaloniaEdit本体（Themes/Editor.axaml）・diff表示・JSON直接編集タブ等、
/// 「コード扱いの文字」を表示する箇所も明示的にDynamicResourceで参照している
/// （grep FontFamily="{DynamicResource CodeFontFamily}" で洗い出し済み）。
/// </summary>
public static class AppFontManager
{
    private const string UiFontKey = "UiFontFamily";
    private const string BodyTextFontKey = "BodyTextFontFamily";
    private const string CodeFontKey = "CodeFontFamily";

    /// <summary>
    /// UIフォントを設定する（メニュー・ボタン・一覧など画面全体のUI文字に効く。旧称
    /// SetBodyFontFamily。「本文」という名前が実態（UI全体）と合っていなかったため
    /// v1.0.21で改名した）。<paramref name="familyName"/>がnull・空・空白のみの場合は
    /// 既定（Tokens.axamlのUiFontFamily）へ戻す。
    /// </summary>
    public static void SetUiFontFamily(string? familyName) => SetOrReset(UiFontKey, familyName);

    /// <summary>
    /// 本文フォントを設定する（v1.0.21で新設。取扱説明書・Markdownプレビューの地の文にのみ効く。
    /// <see cref="SetUiFontFamily"/>とは別枠）。<paramref name="familyName"/>がnull・空・
    /// 空白のみの場合は既定（Tokens.axamlのBodyTextFontFamily。UiFontFamilyと同じ
    /// フォールバック列）へ戻す。
    /// </summary>
    public static void SetBodyTextFontFamily(string? familyName) => SetOrReset(BodyTextFontKey, familyName);

    /// <summary>
    /// 等幅（コード用）フォントを設定する。<paramref name="familyName"/>がnull・空・空白のみの
    /// 場合は既定（Tokens.axamlのCodeFontFamily）へ戻す。
    /// </summary>
    public static void SetCodeFontFamily(string? familyName) => SetOrReset(CodeFontKey, familyName);

    private static void SetOrReset(string key, string? familyName)
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(familyName))
        {
            // Removeはキーが無くても例外にならない（ResourceDictionaryの仕様）。
            app.Resources.Remove(key);
        }
        else
        {
            // FontFamilyのコンストラクタはCSS文字列を組み立てるわけではなく、渡した文字列を
            // そのままファミリ名として保持するだけのため、'・\ を含むフォント名でも
            // エスケープ処理は不要（Paneがsettings.js側で行っていたCSS文字列組み立て時の
            // エスケープは、Avalonia側の構造上そもそも発生しない。PR説明参照）。
            app.Resources[key] = new FontFamily(familyName);
        }
    }
}
