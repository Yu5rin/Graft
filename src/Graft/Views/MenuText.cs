namespace Graft.Views;

/// <summary>
/// 右クリックメニュー・ツールバーのメニューで共通に使う項目名。
///
/// 同じ操作がエクスプローラ・エディタのタブ・検索結果の3か所に並ぶのに、以前は「パスをコピー／
/// フルパスをコピー」「エクスプローラで表示／ファイルマネージャで表示」のように、場所ごとに
/// 言い方が違っていた。項目名を1か所に集め、XAML からは <c>{x:Static views:MenuText.…}</c> で
/// 参照する（言い方を変えるときに、直し忘れが出ない）。
///
/// 【OSで出し分ける理由】 Windows には「エクスプローラー」があるが、Linux のファイルマネージャは
/// 環境ごとに名前が違う（Nautilus・Dolphin など）。Windows でも「ファイルマネージャー」と出すと
/// 馴染みが無く、Linux で「エクスプローラー」と出すと存在しないアプリを指してしまうため、
/// 起動時に一度だけ OS を見て切り替える。
/// </summary>
public static class MenuText
{
    /// <summary>ファイルのフルパスをクリップボードへコピーする。</summary>
    public const string CopyPath = "パスをコピー";

    /// <summary>プロジェクトのフォルダからの相対パスをクリップボードへコピーする。</summary>
    public const string CopyRelativePath = "相対パスをコピー";

    /// <summary>OSのファイルマネージャでファイルを選択表示する。Windows は「エクスプローラー」、それ以外は「ファイルマネージャー」。</summary>
    public static readonly string RevealInFileManager =
        OperatingSystem.IsWindows() ? "エクスプローラーで表示" : "ファイルマネージャーで表示";

    /// <summary>ファイルを「AIに渡すファイルを選ぶ」画面で「内容も出す」にする。</summary>
    public const string AddToContext = "AIに渡すファイルに追加";
}
