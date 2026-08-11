using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Graft.Platform;

namespace Graft.Views;

/// <summary>
/// 取扱説明書機能: F1・ツールバーの「?」メニュー「取扱説明書」の専用ウィンドウ。
/// <see cref="ShortcutsWindow"/>・<see cref="LogViewerWindow"/>・<see cref="OpenSourceLicensesWindow"/>
/// と同じく静的表示が主体のためViewModelは持たない。
///
/// 【本文の持ち方】
/// <c>docs/取扱説明書.md</c> を埋め込みリソースとしてアセンブリへ同梱する（Graft.csproj参照）。
/// 発行設定（PublishSingleFile=true）では発行フォルダに含めるファイルを実行ファイル本体・
/// ネイティブDLL3つの計4ファイルへ絞り込んでおり（Graft.csprojのコメント参照）、
/// <c>docs/</c> フォルダそのものは配布物に含まれない。そのため、外部ファイルとして
/// 参照する経路（<see cref="IFileManagerLauncher"/>でOSの既定アプリから開く等）は
/// 配布後の実機で「ファイルが見つからない」事故になる。オープンソースライセンス表記
/// （<see cref="OpenSourceLicensesWindow"/>）と同じ理由・同じ方式で埋め込みリソース化した。
///
/// 【表示の装飾】
/// 利用者からの指摘対応: 埋め込んだMarkdownをそのままプレーンテキスト表示していたが、
/// 見出し・箇条書き・表などが視覚的に分かるよう装飾表示してほしいとの指摘を受けた。
/// <see cref="ManualMarkdownRenderer"/>（自前の軽量パーサ。外部ライブラリは追加しない方針）で
/// Avalonia標準コントロールへ組み立て、<see cref="ManualContentPanel"/>へ流し込む。
/// 目次（Markdown内の[見出し名](#アンカー)）はクリックで該当見出しへスクロールする
/// （<see cref="JumpToAnchor"/>）。
/// </summary>
public partial class ManualWindow : Window
{
    /// <summary>埋め込みリソース名。Graft.csprojのLogicalNameと一致させる。</summary>
    internal const string ResourceName = "Graft.Docs.取扱説明書.md";

    private readonly string _manualText;
    private readonly IReadOnlyDictionary<string, Control> _anchors;

    public ManualWindow()
    {
        InitializeComponent();
        _manualText = LoadManualText();

        var rendered = ManualMarkdownRenderer.Render(_manualText, JumpToAnchor);
        _anchors = rendered.Anchors;
        foreach (var block in rendered.Blocks)
        {
            ManualContentPanel.Children.Add(block);
        }

        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
        // 細かいユーザビリティ改善5: 本文は読み取り専用の閲覧欄のため、初期フォーカスは既定ボタンへ。
        Loaded += (_, _) => CloseButton.Focus();
    }

    /// <summary>埋め込みリソースから取扱説明書の本文（生のMarkdown）を読み込む。読み込めない場合は利用者に分かる文言を返す。</summary>
    private static string LoadManualText()
    {
        try
        {
            using var stream = typeof(ManualWindow).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null) return "取扱説明書を読み込めませんでした。";

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return "取扱説明書を読み込めませんでした。";
        }
    }

    /// <summary>目次から該当見出しへスクロールする。未知のアンカーは何もしない（壊れたリンクで例外にしない）。</summary>
    private void JumpToAnchor(string anchor)
    {
        if (_anchors.TryGetValue(anchor, out var target))
        {
            target.BringIntoView();
        }
    }

    private void OnCopyClicked(object? sender, RoutedEventArgs e)
        => AvaloniaUiServices.SharedClipboard.SetText(_manualText);

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
