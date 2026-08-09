using System.IO;
using Graft.Infra;
using Graft.Platform;

namespace Graft.ViewModels;

/// <summary>1枚のエディタタブの復元情報（v2.0 仕様書3.2）。プロジェクト相対パスとカーソル位置のみを保持する。</summary>
public sealed class OpenTabState
{
    /// <summary>プロジェクトルートからの相対パス（区切りは "/"）。</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>カーソル行（1始まり）。</summary>
    public int CaretLine { get; set; } = 1;

    /// <summary>カーソル列（1始まり）。</summary>
    public int CaretColumn { get; set; } = 1;
}

/// <summary>
/// プロジェクトごとに記憶するレイアウト（v2.0 仕様書3.2）。
/// サイドビュー幅・接ぎ木パネル高さ・エディタのフォントサイズに加え、
/// 開いていたエディタタブの構成とエクスプローラの展開状態を保持する（フェーズE2）。
/// エディタ領域は残りを使う可変領域のため、ペイン寸法そのものは保存対象にしない。
/// </summary>
public sealed class ProjectPaneLayout
{
    /// <summary>サイドビュー（エクスプローラ・プロジェクト・履歴・検索）の幅。</summary>
    public double SideViewWidth { get; set; } = 260;

    /// <summary>下部の接ぎ木パネルの高さ（下配置のとき）。</summary>
    public double GraftPanelHeight { get; set; } = 260;

    /// <summary>
    /// 接ぎ木パネルの配置。"bottom"（既定・コードの下）または "right"（コードの右、3列）。
    /// 後方互換: このキーの無い既存のlayout.json（このフィールドが追加される前に保存されたもの）を
    /// 読んでもJSONの欠落フィールドは既定値へ倒れるため、自然に下配置として復元される
    /// （ShellViewModel.ParseGraftPanelPlacementも未知の値を下配置として扱う二重の備え）。
    /// </summary>
    public string GraftPanelPlacement { get; set; } = "bottom";

    /// <summary>
    /// 右配置のときの接ぎ木パネルの幅。既定の460pxは実機検証（Xvfb）で「適用」ボタンまで
    /// 含めたヘッダーの全ボタンが収まることを確認した最小幅（ShellWindow.GraftPanelMinWidth、
    /// 420px）に余白分を足した値（ShellWindow.axaml.cs参照）。
    /// </summary>
    public double GraftPanelWidth { get; set; } = 460;

    /// <summary>コード表示のフォントサイズ（v2.0 仕様書3.2・4.4）。</summary>
    public double CodeFontSize { get; set; } = 13;

    /// <summary>
    /// v1.5 の3ペインレイアウトで使っていた列幅。既存の layout.json を読んでも
    /// 情報を失わないよう残すが、v2.0 のシェルでは参照しない。
    /// </summary>
    public double ProjectColumnWidth { get; set; } = 260;

    /// <summary>v1.5 の中央「ブロック一覧」列の幅。v2.0 のシェルでは参照しない。</summary>
    public double BlockColumnWidth { get; set; } = 380;

    /// <summary>
    /// 開いていたエディタタブ（プロジェクト相対パスの一覧・各タブのカーソル位置）。
    /// 順序はタブの並び順を表す（仕様書3.2）。
    /// </summary>
    public List<OpenTabState> OpenTabs { get; set; } = new();

    /// <summary>アクティブだったタブのプロジェクト相対パス。該当タブが無ければ復元しない。</summary>
    public string? ActiveTabPath { get; set; }

    /// <summary>エクスプローラで展開されていたフォルダのプロジェクト相対パスの一覧（仕様書3.2・4.2）。</summary>
    public List<string> ExpandedFolders { get; set; } = new();
}

/// <summary>
/// layout.json の内容全体（仕様書8.11）。
/// ウィンドウ位置・サイズ・最大化状態・左ペイン上下比率・プロジェクト別ペイン幅を保持する。
/// </summary>
public sealed class WindowLayoutState
{
    /// <summary>ウィンドウ左端。未保存時は null（初回起動を示す）。</summary>
    public double? Left { get; set; }

    /// <summary>ウィンドウ上端。未保存時は null。</summary>
    public double? Top { get; set; }

    public double Width { get; set; } = 1280;

    public double Height { get; set; } = 800;

    public bool IsMaximized { get; set; }

    /// <summary>左ペイン内のプロジェクト一覧（上段）が占める比率（0〜1）。残りを履歴が使う。</summary>
    public double LeftPaneSplitRatio { get; set; } = 0.55;

    /// <summary>プロジェクトIDをキーとしたペイン幅。仕様書8.2「ペイン幅はプロジェクトごとに記憶する」。</summary>
    public Dictionary<string, ProjectPaneLayout> ProjectPaneWidths { get; set; } = new();
}

/// <summary>
/// ウィンドウ・ペインレイアウトの永続化（仕様書8.11）。
/// 保存先は <c>AppPaths.BaseDirectory</c> 配下の <c>layout.json</c>
/// （<see cref="AppPaths"/> 自体には layout.json 用のプロパティが無いため、ここで組み立てる）。
/// </summary>
public sealed class WindowLayoutStore
{
    private readonly string _filePath;
    private readonly JsonFileStore _store = new();

    public WindowLayoutStore(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _filePath = Path.Combine(paths.BaseDirectory, "layout.json");
    }

    /// <summary>layout.json を読み込む。存在しない・破損している場合は既定値から再生成する。</summary>
    public async Task<WindowLayoutState> LoadAsync(CancellationToken ct = default)
    {
        var result = await _store
            .ReadWithRecoveryAsync(_filePath, static () => new WindowLayoutState(), JsonFileStore.DefaultOptions, ct)
            .ConfigureAwait(false);
        return result.Value;
    }

    /// <summary>layout.json を書き込む。</summary>
    public Task SaveAsync(WindowLayoutState state, CancellationToken ct = default)
        => _store.WriteAsync(_filePath, state, JsonFileStore.DefaultOptions, ct);

    /// <summary>
    /// 保存された位置・サイズから実際にウィンドウへ適用すべき矩形を求める。
    /// 現在のモニタ構成（仮想画面全体）でタイトルバー付近が画面外になる場合は
    /// プライマリモニタの作業領域中央へ補正する（仕様書8.11）。
    /// 画面構成の問い合わせは<see cref="IScreenInfo"/>経由とし、WPF/Avalonia双方の
    /// 呼び出し側から同じロジックを共有できるようにする（仕様書19章・20章 L3）。
    /// </summary>
    public static UiRect ResolveWindowBounds(WindowLayoutState state, double minWidth, double minHeight, IScreenInfo screens)
    {
        ArgumentNullException.ThrowIfNull(screens);
        var width = Math.Max(state.Width, minWidth);
        var height = Math.Max(state.Height, minHeight);

        if (state.Left is not { } left || state.Top is not { } top || !IsReachable(left, top, width, screens))
        {
            return CenterOnPrimary(width, height, screens);
        }

        return new UiRect(left, top, width, height);
    }

    /// <summary>
    /// タイトルバー相当の矩形が現在の仮想画面（全モニタの合成領域）に一部でも
    /// かかっていれば「到達可能」とみなす。モニタ構成の変化（取り外し等）で
    /// 完全に画面外へ追いやられているケースだけを補正対象とする。
    /// </summary>
    private static bool IsReachable(double left, double top, double width, IScreenInfo screens)
    {
        var virtualScreen = screens.VirtualScreen;
        var titleBar = new UiRect(left, top, Math.Min(width, 200), 40);
        return virtualScreen.IntersectsWith(titleBar);
    }

    /// <summary>
    /// プライマリモニタの作業領域中央へ配置する。作業領域が取得できない場合
    /// （幅か高さが0。ウィンドウマネージャの無い環境や、画面情報の準備が整う前の
    /// 問い合わせで起こりうる）は、要求されたサイズをそのまま維持し位置だけ原点に置く。
    /// ここで作業領域の大きさで単純に切り詰めると、サイズが0へ潰れたうえで最小サイズまで
    /// しか戻らず、ウィンドウが常に最小サイズで開いてしまう。
    /// </summary>
    private static UiRect CenterOnPrimary(double width, double height, IScreenInfo screens)
    {
        var workArea = screens.PrimaryWorkArea;
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            return new UiRect(0, 0, width, height);
        }

        var boundedWidth = Math.Min(width, workArea.Width);
        var boundedHeight = Math.Min(height, workArea.Height);
        var left = workArea.Left + Math.Max(0, (workArea.Width - boundedWidth) / 2);
        var top = workArea.Top + Math.Max(0, (workArea.Height - boundedHeight) / 2);
        return new UiRect(left, top, boundedWidth, boundedHeight);
    }

    /// <summary>指定プロジェクトのペイン幅設定を取得する。無ければ既定値で作成して登録する。</summary>
    public static ProjectPaneLayout GetOrCreatePaneLayout(WindowLayoutState state, string projectId)
    {
        if (!state.ProjectPaneWidths.TryGetValue(projectId, out var layout))
        {
            layout = new ProjectPaneLayout();
            state.ProjectPaneWidths[projectId] = layout;
        }
        return layout;
    }
}
