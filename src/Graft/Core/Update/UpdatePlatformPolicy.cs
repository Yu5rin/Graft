namespace Graft.Core.Update;

/// <summary>自動更新の判断に使う、実行中のOSの区分。</summary>
public enum UpdatePlatform
{
    /// <summary>Windows（win-x64版。<c>Graft-&lt;版&gt;-win-x64.zip</c>で配布）。</summary>
    Windows,

    /// <summary>Linux（linux-x64版。<c>Graft-&lt;版&gt;-linux-x64.tar.gz</c>で配布）。</summary>
    Linux,

    /// <summary>上のどちらでもない（macOS等。配布物そのものが無い）。</summary>
    Unsupported,
}

/// <summary>
/// 「どのOSで、どの添付ファイルを選び、自動の入れ替えまで提供するか」の判断だけを集めたもの。
/// OSの判定結果（<see cref="OperatingSystem.IsWindows"/>等）は呼び出し側が引数で渡し、ここでは
/// 通信・ファイル・時刻に一切触れない（<c>UpdatePlatformPolicyTests</c>で固定している）。
///
/// 【なぜ切り出したか（不具合: Linuxでは自動更新が必ず失敗して巻き戻っていた）】
/// 以前はOSによる分岐がどこにも無く、Linuxで「今すぐ更新」を押しても、
/// <list type="number">
/// <item>添付ファイルは常に<c>-win-x64.zip</c>（Windows版）を選んでダウンロードし、</item>
/// <item>ZIPの中身の検査（<see cref="UpdateZipInspector"/>）はWindows版の6ファイルを
/// 前提に通り、</item>
/// <item>入れ替え（<see cref="SelfUpdateInstaller"/>）が実行中のフォルダで<c>Graft.exe</c>を
/// 退避しようとして、Linuxには<c>Graft.exe</c>が無い（実行ファイルは拡張子なしの<c>Graft</c>）
/// ため失敗し、巻き戻して「更新に失敗しました」を出す</item>
/// </list>
/// という経過を毎回たどっていた。利用者から見ると「更新ボタンを押すと必ず失敗する」状態だった。
///
/// 【Linuxで自動の入れ替えを提供しない理由（2026-09-25に配布物の実物を確認して判断）】
/// <list type="bullet">
/// <item>Linux版はzipではなくtar.gz（<c>Graft-1.0.20-linux-x64.tar.gz</c>等）で配布している。
/// 現在の検査・展開（<see cref="UpdateZipInspector"/>）・入れ替え対象の一覧
/// （<see cref="UpdateFiles.RequiredFileNames"/>）・次回起動時の後始末
/// （<see cref="PendingUpdateCleanup"/>）はすべてWindows版のzip・ファイル名に固定されており、
/// Linux対応はtarの読み取り（シンボリックリンク・ハードリンク・パスの外への書き出しを拒む
/// 検査を含む）を新たに作ることになる。安全側の検査を二重に持つ大きな追加であり、
/// 「直す」範囲を超える。</item>
/// <item><c>tools/New-Release.ps1</c>はWindows上でtar.gzを作るため、実際に配布している
/// v1.0.20のtar.gzでは<c>Graft</c>の属性が<c>-rw-rw-rw-</c>（実行権限なし）だった。
/// そのまま入れ替えると再起動できなくなるため、入れ替え側で実行権限を付け直す処理まで
/// 要る。これをこの開発環境から実機相当で確かめる手段が無い。</item>
/// </list>
/// そのためLinuxでは「新しい版があります」とリリースページへの案内だけを出し、
/// 失敗すると分かっている「今すぐ更新」ボタンは出さない（<see cref="CanSelfInstall"/>）。
/// 将来Linuxでも入れ替えを実装する場合は、<see cref="CanSelfInstall"/>を変える前に上の2点を
/// 解決すること。
/// </summary>
public static class UpdatePlatformPolicy
{
    /// <summary>
    /// OSの判定結果から<see cref="UpdatePlatform"/>を決める。引数は通常
    /// <c>OperatingSystem.IsWindows()</c>・<c>OperatingSystem.IsLinux()</c>をそのまま渡す。
    /// </summary>
    public static UpdatePlatform Detect(bool isWindows, bool isLinux)
    {
        if (isWindows) return UpdatePlatform.Windows;
        if (isLinux) return UpdatePlatform.Linux;
        return UpdatePlatform.Unsupported;
    }

    /// <summary>
    /// ダウンロードからファイルの入れ替え・再起動までを自動で行えるか。Windowsだけtrue。
    /// falseのOSでは、新しい版があることとリリースページの場所だけを案内する
    /// （理由はクラスコメント参照）。
    /// </summary>
    public static bool CanSelfInstall(UpdatePlatform platform) => platform == UpdatePlatform.Windows;

    /// <summary>
    /// そのOS向けの添付ファイル名の末尾。配布物が無いOSではnull。
    /// 版の部分（<c>Graft-</c>と末尾の間）に<c>"v"</c>が付くかどうかに関わらず一致させるため、
    /// 添付ファイルの選択はこの末尾だけで行う（<see cref="SelectAsset"/>参照）。
    /// </summary>
    public static string? AssetNameSuffix(UpdatePlatform platform) => platform switch
    {
        UpdatePlatform.Windows => UpdateFiles.WindowsAssetNameSuffix,
        UpdatePlatform.Linux => UpdateFiles.LinuxAssetNameSuffix,
        _ => null,
    };

    /// <summary>
    /// タグから、そのOS向けの添付ファイル名を組み立てる。配布物が無いOSではnull。
    ///
    /// 【名前の規則（単一の情報源）】 <c>Graft-&lt;タグの先頭の"v"/"V"を1つだけ除いた値&gt;&lt;末尾&gt;</c>。
    /// 例: タグ<c>v1.0.20</c> → <c>Graft-1.0.20-win-x64.zip</c> / <c>Graft-1.0.20-linux-x64.tar.gz</c>。
    /// <c>tools/New-Release.ps1</c>（<c>"Graft-$resolvedVersion-win-x64.zip"</c>。
    /// <c>$resolvedVersion</c>はGraft.csprojの<c>&lt;Version&gt;</c>で"v"なし）と、
    /// <c>.github/workflows/release.yml</c>（タグから"v"を除いた<c>VERSION</c>を使う）の
    /// 両方がこの規則で添付ファイルを作る。
    ///
    /// 【不具合の経緯（添付ファイル名の食い違い）】 かつて<c>release.yml</c>はタグそのもの
    /// （"v"付き）をファイル名に使っており、<c>Graft-v1.0.1-win-x64.zip</c>のような名前を作っていた
    /// （実際にv1.0.0・v1.0.1の添付はこの名前。v1.0.2以降は<c>tools/New-Release.ps1</c>で作られ
    /// "v"なし）。GitHub APIに届かないときの経路（<see cref="UpdateAtomFeedLogic.TryBuildDownloadUrl"/>）は
    /// この規則でURLを組み立てるため、ワークフローで作ったリリースに対しては404になっていた。
    /// ワークフロー側をこの規則に揃えて直した。
    /// </summary>
    public static string? BuildAssetFileName(string tag, UpdatePlatform platform)
    {
        var suffix = AssetNameSuffix(platform);
        if (suffix is null) return null;
        var stripped = tag.Length > 0 && (tag[0] == 'v' || tag[0] == 'V') ? tag[1..] : tag;
        return $"Graft-{stripped}{suffix}";
    }

    /// <summary>
    /// リリースの添付ファイルから、そのOS向けのものを選ぶ。見つからない・配布物が無いOSではnull。
    /// 末尾（<see cref="AssetNameSuffix"/>）だけで選ぶので、<c>Graft-1.0.20-win-x64.zip</c>
    /// （<c>tools/New-Release.ps1</c>の名前）も<c>Graft-v1.0.1-win-x64.zip</c>（過去に
    /// ワークフローが作った名前）も受け付ける。GitHub APIから添付の一覧が取れる経路では、
    /// 名前の"v"の有無で更新が止まることはない。
    /// </summary>
    public static GitHubReleaseAsset? SelectAsset(GitHubReleaseInfo release, UpdatePlatform platform)
    {
        var suffix = AssetNameSuffix(platform);
        return suffix is null ? null : release.FindAssetByNameSuffix(suffix);
    }
}
