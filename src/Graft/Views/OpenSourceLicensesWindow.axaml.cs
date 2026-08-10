using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Graft.Views;

/// <summary>
/// 機能1（オープンソースライセンス表記）でOpenSourceLicensesWindowの一覧に表示する1項目分の
/// 表示用データ。ViewModelではなく（このウィンドウ自体がViewModelを持たない静的表示のため）、
/// 内容はすべて構築時に確定する不変のPOCO。
/// </summary>
public sealed class OpenSourceLicensesWindowEntry
{
    public required string Name { get; init; }
    public required string LicenseAndCopyright { get; init; }
    public required string Summary { get; init; }
    public required string LicenseText { get; init; }

    public string ExpanderAutomationName => $"{Name}のライセンス全文を表示";
    public string LicenseTextAutomationName => $"{Name}のライセンス全文";
}

/// <summary>
/// 機能1（オープンソースライセンス表記）。設定画面「バージョン情報」タブ
/// （<see cref="AboutView"/>）の「オープンソースライセンスを表示」から開く一覧ウィンドウ。
///
/// 【依存関係の調べ方】
/// <c>dotnet list package --include-transitive</c> を <c>src/Graft/Graft.csproj</c> に対して
/// 実行し、直接参照（PackageReference）と、そこから解決される推移的依存を実際に列挙した
/// （思い込みで書かない）。得られた各パッケージについて、NuGetキャッシュ内の .nuspec
/// （&lt;license&gt;・&lt;copyright&gt;・&lt;authors&gt;）でライセンス種別と著作権者を確認し、
/// さらに可能な範囲で該当プロジェクトの実際のリポジトリ（GitHub）のLICENSEファイルを取得して
/// 突き合わせた。両者が食い違う場合（例: AvaloniaEditはnuspec上の著作権表示が
/// 「The AvaloniaUI Project」だが、実際のリポジトリのLICENSEファイルは移植者個人の
/// 「Eli Arbel」名義）は、より原本に近いリポジトリ側のLICENSEファイルの文言を採用した。
///
/// 【実際に確認した内容（グループ化した理由）】
/// パッケージ数が多いため、同一ライセンス・同一著作権者のものはここで1項目にまとめている。
/// - Avalonia／Avalonia.Desktop／Avalonia.Win32／Avalonia.Themes.Fluent／
///   Avalonia.Themes.Simple／Avalonia.Skia／Avalonia.X11／Avalonia.Native／
///   Avalonia.FreeDesktop／Avalonia.Remote.Protocol／Avalonia.Controls.ColorPicker／
///   Avalonia.Controls.DataGrid: MIT、Copyright (c) AvaloniaUI OÜ
///   （AvaloniaUI/Avalonia リポジトリの licence.md より）。
/// - Avalonia.AvaloniaEdit: MIT、Copyright (c) 2017 Eli Arbel
///   （AvaloniaUI/AvaloniaEdit リポジトリの LICENSE より）。
/// - MicroCom.Runtime（AvaloniaのWindows COM相互運用で使用）: MIT、
///   Copyright (c) 2021 Nikita Tsukanov。
/// - SkiaSharp／HarfBuzzSharp（およびそれぞれのNativeAssets各種。Avaloniaの描画・文字整形で
///   使用）: MIT、Copyright (c) 2015-2016 Xamarin, Inc. / 2017-2018 Microsoft Corporation
///   （mono/SkiaSharp リポジトリの LICENSE.md より。HarfBuzzSharpも同一リポジトリに同居するため
///   NativeAssets各種を含め個別のnuspec確認は本体と同一表記であることの確認に留めた）。
/// - System.Text.Encoding.CodePages／Microsoft.Win32.SystemEvents／System.IO.Pipelines
///   （.NETランタイムの構成パッケージ）: MIT、Copyright (c) .NET Foundation and Contributors
///   （dotnet/runtime リポジトリの LICENSE.TXT より）。
/// - Tmds.DBus.Protocol（LinuxのD-Bus通信、Avalonia.FreeDesktop経由でトレイアイコン等に使用）:
///   MIT、Copyright (c) 2021 Tom Deseyn。
/// - Avalonia.Angle.Windows.Natives（Windows版のみ。GPU描画向けのANGLEネイティブライブラリ）:
///   BSD 3条項ライセンス、Copyright 2018 The ANGLE Project Authors。nuspec上は
///   license type="file" でパッケージ自体にLICENSEファイルが同梱されており、その内容を
///   そのまま採用した（最も原本に近いソース）。
/// - DiffPlex（差分計算。既存の表記を踏襲）: Apache License 2.0、作者 Matthew Manela。
///
/// 【対象外にしたもの（配布物に含まれないため）】
/// - Avalonia.BuildServices・Microsoft.NET.ILLink.Tasks:
///   NuGetキャッシュ内を確認したところ lib/ フォルダを持たず、build/・tools/ のみで
///   構成されるビルド時専用パッケージ（MSBuildタスク）と判断した。実際に
///   <c>dotnet publish -c Release -r linux-x64</c> を実行して発行結果を確認したが、
///   これらのDLLは出力に含まれなかった（Graft本体に埋め込まれる管理アセンブリと、
///   ネイティブのlibSkiaSharp.so／libHarfBuzzSharp.soのみが出力された）。
/// - Avalonia.Diagnostics: <c>Graft.csproj</c> で <c>Condition="'$(Configuration)'=='Debug'"</c>
///   が付いており、配布に使うRelease構成では参照されない。
///
/// 【確認できなかったこと】
/// 各リポジトリのLICENSEファイルはdefaultブランチ（clone時点の最新コミット）から取得しており、
/// Graftが実際に参照するバージョン時点のLICENSEファイルと完全一致することまでは保証できない
/// （MITライセンスの本文自体は標準的な定型文であり、通常はバージョン間で変化しない性質のものだが、
/// コミット単位での完全一致までは確認していない）。
/// </summary>
public partial class OpenSourceLicensesWindow : Window
{
    private readonly record struct LicenseSource(
        string Name, string LicenseKind, string Copyright, string Summary, string ResourceName);

    private static readonly LicenseSource[] Sources =
    {
        new(
            "Avalonia（UIフレームワーク本体・関連コンポーネント）",
            "MIT License",
            "Copyright (c) AvaloniaUI OÜ",
            "画面表示の基盤となるクロスプラットフォームUIフレームワーク本体、および付随するテーマ・入力・" +
            "プラットフォーム連携コンポーネント一式（Avalonia.Desktop、Avalonia.Win32、Avalonia.Themes.Fluent、" +
            "Avalonia.Themes.Simple、Avalonia.Skia、Avalonia.X11、Avalonia.Native、Avalonia.FreeDesktop、" +
            "Avalonia.Remote.Protocol、Avalonia.Controls.ColorPicker、Avalonia.Controls.DataGridを含む）。",
            "Graft.Assets.Avalonia-LICENSE.txt"),
        new(
            "Avalonia.AvaloniaEdit",
            "MIT License",
            "Copyright (c) 2017 Eli Arbel",
            "コードエディタ本体（4章）に使用するテキスト編集コンポーネント。WPF版AvalonEditのAvalonia移植。",
            "Graft.Assets.AvalonEdit-LICENSE.txt"),
        new(
            "MicroCom.Runtime",
            "MIT License",
            "Copyright (c) 2021 Nikita Tsukanov",
            "AvaloniaがWindowsのCOM相互運用（シェル連携等）に内部で使用するランタイムライブラリ。",
            "Graft.Assets.MicroCom-LICENSE.txt"),
        new(
            "SkiaSharp / HarfBuzzSharp",
            "MIT License",
            "Copyright (c) 2015-2016 Xamarin, Inc. / 2017-2018 Microsoft Corporation",
            "Avaloniaの描画（SkiaSharp）と文字の整形・シェーピング（HarfBuzzSharp）に使用するグラフィックス" +
            "ライブラリ。プラットフォームごとのネイティブ実装（NativeAssets）を含む。",
            "Graft.Assets.SkiaSharp-LICENSE.txt"),
        new(
            ".NETランタイム構成パッケージ",
            "MIT License",
            "Copyright (c) .NET Foundation and Contributors",
            "System.Text.Encoding.CodePages（Shift_JIS等コードページの判定）、" +
            "Microsoft.Win32.SystemEvents（Windowsのテーマ変更通知）、" +
            "System.IO.Pipelines（高性能I/O）。",
            "Graft.Assets.DotnetRuntime-LICENSE.txt"),
        new(
            "Tmds.DBus.Protocol",
            "MIT License",
            "Copyright (c) 2021 Tom Deseyn",
            "LinuxでのD-Bus通信（トレイアイコン等のデスクトップ連携、Avalonia.FreeDesktop経由）に使用する。",
            "Graft.Assets.TmdsDBus-LICENSE.txt"),
        new(
            "Avalonia.Angle.Windows.Natives（Windows版のみ）",
            "BSD 3条項ライセンス（ANGLE Project）",
            "Copyright 2018 The ANGLE Project Authors.",
            "Windows版でのみ同梱。GPUによる高速描画（Direct3D経由のOpenGL ES互換レイヤー）に使用する" +
            "ネイティブライブラリ。",
            "Graft.Assets.Angle-LICENSE.txt"),
        new(
            "DiffPlex",
            "Apache License 2.0",
            "作者: Matthew Manela",
            "パッチ適用の内部で使用する差分計算ライブラリ。",
            "Graft.Assets.DiffPlex-LICENSE.txt"),
    };

    public OpenSourceLicensesWindow()
    {
        InitializeComponent();
        EntriesList.ItemsSource = Sources.Select(BuildEntry).ToList();
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
        // 細かいユーザビリティ改善5: 入力欄が無いウィンドウのため、初期フォーカスは既定ボタンへ。
        Loaded += (_, _) => CloseButton.Focus();
    }

    private static OpenSourceLicensesWindowEntry BuildEntry(LicenseSource source) => new()
    {
        Name = source.Name,
        LicenseAndCopyright = $"ライセンス: {source.LicenseKind}　{source.Copyright}",
        Summary = source.Summary,
        LicenseText = LoadLicenseText(source.ResourceName),
    };

    // 発行フォルダに含めるファイルを増やさないよう、ライセンス全文は埋め込みリソースとして持つ
    // （AboutView.axaml.csのLoadLicenseTextAsyncと同じ方針）。本ウィンドウは開いた時点で全項目を
    // まとめて表示するため、非同期化する意味が薄く、ファイルサイズも小さいため同期読み込みで揃える。
    private static string LoadLicenseText(string resourceName)
    {
        try
        {
            using var stream = typeof(OpenSourceLicensesWindow).Assembly.GetManifestResourceStream(resourceName);
            if (stream is null) return "ライセンスファイルを読み込めませんでした。";

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return "ライセンスファイルを読み込めませんでした。";
        }
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
