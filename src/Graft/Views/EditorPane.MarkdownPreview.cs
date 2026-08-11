using System.IO;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit.Document;
using Graft.Platform;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// <see cref="EditorPane"/> の分割ファイル（1ファイル400行上限のため）。
/// Markdownプレビュー機能（利用者指示）を担う。
///
/// 【モード管理】プレビュー/編集の状態そのものは<see cref="EditorTabViewModel.ShowMarkdownPreview"/>
/// （タブごとに保持。一度編集にしたタブはそのタブが開いている間は編集のまま）が真実の情報源で、
/// 本ファイルはそれを見て<see cref="Editor"/>と<see cref="MarkdownPreviewHost"/>のどちらを表示するか
/// （<see cref="ApplyMarkdownPreviewMode"/>）・切替時のスクロール位置の受け渡し
/// （<see cref="EditorTabViewModel.CaretLine"/>を「現在おおよそ見ている行」の橋渡しとして再利用する。
/// <see cref="CaptureMarkdownTransitionLine"/>参照）を担当する。
///
/// 【プレビュー本文は常に編集中バッファから描画】<see cref="RenderMarkdownPreview"/>は必ず
/// <c>tab.Session.Document.Text</c>（AvaloniaEditの編集用バッファ）を渡す。ディスクを読み直さない
/// ため、保存前の編集内容がそのままプレビューに反映される（利用者指示の追加要件4）。
/// パッチ適用後の再読込・外部からのDocument書き換えにも追従できるよう、.mdタブ読み込み中は
/// <see cref="TextDocument.Changed"/>を購読し、プレビュー表示中であれば再描画する
/// （<see cref="OnDocumentChangedForMarkdownPreview"/>）。
/// </summary>
public partial class EditorPane
{
    /// <summary>
    /// テスト用の差し替え口。既定は本番と同じ経路（<see cref="AvaloniaDialogService"/>による
    /// 確認・メッセージダイアログ）。<see cref="AvaloniaDialogService"/>が組み立てる確認ダイアログは
    /// 動的に<c>Window</c>を生成するだけで外部から参照する手段が無く、ヘッドレステストから
    /// 実際にボタン操作するのは難しい（<c>DialogKeyboardCoverageTests</c>のコメントに同じ制約の
    /// 記載がある）。「確認してから開く」という順序自体は利用者指示で必須のテスト対象のため、
    /// <c>Graft.AssemblyInfo</c>の<c>InternalsVisibleTo</c>でGraft.UiTestsへ公開したこの
    /// 差し替え口経由でテストする。
    /// </summary>
    internal IDialogService MarkdownLinkDialogs { get; set; } = new AvaloniaDialogService();

    /// <summary>
    /// テスト用の差し替え口。既定は本番と同じ経路（<see cref="PlatformServices.Current"/>の
    /// <see cref="IExternalLinkLauncher"/>でブラウザを起動）。上記<see cref="MarkdownLinkDialogs"/>と
    /// 同じ理由で、確認後に実際に「開く」操作が呼ばれたかどうかをテストから検証できるようにする。
    /// </summary>
    internal Action<string> OpenExternalLinkAction { get; set; } = url => PlatformServices.Current.ExternalLinks.Open(url);

    /// <summary>
    /// プレビュー/編集の表示を、現在の<see cref="_loadedTab"/>の状態へ合わせる。
    /// タブ切替（<see cref="ApplyDocumentTab"/>）・モード切替
    /// （<see cref="EditorTabViewModel.ShowMarkdownPreview"/>の変化）の双方から呼ばれる。
    /// </summary>
    private void ApplyMarkdownPreviewMode()
    {
        var tab = _loadedTab;
        var showPreview = tab is
        {
            Kind: EditorTabKind.Document,
            IsMarkdownFile: true,
            MarkdownPreviewUnavailable: false,
            ShowMarkdownPreview: true,
        };

        if (!showPreview)
        {
            MarkdownPreviewHost.IsVisible = false;
            Editor.IsVisible = true;
            if (tab is { Kind: EditorTabKind.Document, IsMarkdownFile: true })
            {
                // Markdownタブが編集モードへ切り替わった直後のみ、切替前に捉えた行へカーソルを
                // 合わせ直す（ダブルクリック位置・Escでの切替元の行を反映する）。非Markdownタブ・
                // タブの初回オープン時のカーソル位置はRestoreViewStateFromが既に設定済みのため、
                // ここで重ねて動かす必要は無い。
                MoveCaretTo(tab.CaretLine, tab.CaretColumn);
            }
            Editor.Focus();
            return;
        }

        RenderMarkdownPreview(tab!);
        Editor.IsVisible = false;
        MarkdownPreviewHost.IsVisible = true;
        MarkdownPreviewHost.ScrollToLine(tab!.CaretLine);
    }

    /// <summary>
    /// プレビュー本文を編集中バッファ（<c>tab.Session.Document.Text</c>）から組み立て直す。
    /// リンクの種別ごとの扱いは<see cref="ManualMarkdownRenderer"/>が振り分け、ここでは
    /// 実際のジャンプ・タブオープン・外部起動を行うハンドラだけを渡す。
    /// </summary>
    private void RenderMarkdownPreview(EditorTabViewModel tab)
    {
        MarkdownPreviewHost.Render(
            tab.Session.Document.Text,
            MarkdownPreviewHost.JumpToAnchor,
            OnMarkdownRelativeLinkClicked,
            OnMarkdownExternalLinkClicked);
    }

    /// <summary>
    /// テーマ切替（設定画面でのライト/ダーク切替・システム追従）時に、表示中のMarkdownプレビューを
    /// 再構築する。
    ///
    /// 【なぜ必要か（実機検証で発覚した不具合）】<see cref="ManualMarkdownRenderer"/>が組み立てる
    /// コントロールは<c>DynamicResourceExtension</c>でテーマ色（<c>TextPrimary</c>等）を束ねており、
    /// 本来テーマ切替に自動追従するはずだが、実機（Xvfb）で実際にプレビューを表示したまま
    /// 設定画面からテーマを切り替えると、既に構築済みのブロックの文字が再描画されず
    /// （前景色が旧テーマの色のまま、あるいは背景色と同化して）見えなくなる不具合を確認した
    /// （ヘッドレスUIテストでは実際のピクセル描画を検証していないため検出できなかった）。
    /// <see cref="Graft.Editor.SyntaxHighlightBridge"/>がAvaloniaEdit側で
    /// <c>ThemeManager.ThemeChanged</c>を購読して明示的に<c>Redraw()</c>するのと同じ考え方で、
    /// こちらは表示中であれば<see cref="RenderMarkdownPreview"/>を呼び直して現在のテーマ色で
    /// 組み立て直すことで確実に反映させる（タブ切り替え直後の再構築と同じ経路を使うだけなので
    /// 実装・検証コストが小さい）。
    /// </summary>
    private void OnThemeChangedForMarkdownPreview(object? sender, EventArgs e)
    {
        if (_loadedTab is not { Kind: EditorTabKind.Document, IsMarkdownFile: true } tab) return;
        if (!tab.ShowMarkdownPreview || !MarkdownPreviewHost.IsVisible) return;
        RenderMarkdownPreview(tab);
    }

    /// <summary>
    /// .mdタブ読み込み中のDocument変更（パッチ適用後の再読込等）を購読しての再描画。
    /// 編集モード中や別タブ表示中は何もしない（プレビュー表示中の.mdタブに限る）。
    /// </summary>
    private void OnDocumentChangedForMarkdownPreview(object? sender, DocumentChangeEventArgs e)
    {
        if (_loadedTab is not { Kind: EditorTabKind.Document, IsMarkdownFile: true } tab) return;
        if (!tab.ShowMarkdownPreview || !MarkdownPreviewHost.IsVisible) return;
        RenderMarkdownPreview(tab);
    }

    /// <summary>タブ切替のたびに、前のタブのDocument購読を外す。<see cref="OnUnloaded"/>でも呼ぶ。</summary>
    private void DetachMarkdownDocumentWatch()
    {
        if (_markdownWatchedDocument is not null)
        {
            _markdownWatchedDocument.Changed -= OnDocumentChangedForMarkdownPreview;
        }
        _markdownWatchedDocument = null;
    }

    /// <summary>
    /// 現在表示中の側（プレビュー/編集のどちらか）から「おおよそ今見ている行」を取り出し、
    /// <see cref="EditorTabViewModel.CaretLine"/>へ書き戻す。モード切替の直前に呼ぶことで、
    /// 切替後の表示（<see cref="ApplyMarkdownPreviewMode"/>）がその行へ合わせられる
    /// （利用者指示: 切り替えてもスクロール位置を保つ）。
    /// </summary>
    private void CaptureMarkdownTransitionLine(EditorTabViewModel tab)
    {
        if (MarkdownPreviewHost.IsVisible)
        {
            tab.CaretLine = MarkdownPreviewHost.GetTopVisibleLine();
        }
        else if (Editor.IsVisible)
        {
            tab.CaretLine = GetEditorTopVisibleLine();
        }
    }

    /// <summary>エディタの現在の表示範囲の先頭行（1始まり）を返す。GitGutterProviderと同じAPIで求める。</summary>
    private int GetEditorTopVisibleLine()
    {
        var textView = Editor.TextArea.TextView;
        var line = textView.GetDocumentLineByVisualTop(textView.VerticalOffset);
        return line?.LineNumber ?? 1;
    }

    /// <summary>切替ボタン（EditorPane.axaml「MarkdownModeToggleButton」）のクリック。</summary>
    private void OnToggleMarkdownPreviewClicked(object? sender, RoutedEventArgs e)
    {
        if (_loadedTab is not { Kind: EditorTabKind.Document, IsMarkdownFile: true } tab) return;
        if (tab.MarkdownPreviewUnavailable) return;

        CaptureMarkdownTransitionLine(tab);
        tab.ShowMarkdownPreview = !tab.ShowMarkdownPreview;
    }

    /// <summary>
    /// プレビュー本文のブロックがダブルクリックされた（利用者指示の追加要件3）。編集モードへ
    /// 切り替え、ダブルクリックした段落に対応する行（<paramref name="line"/>、1始まり）へ
    /// カーソルを置く。
    /// </summary>
    private void OnMarkdownBlockDoubleClicked(int line)
    {
        if (_loadedTab is not { Kind: EditorTabKind.Document, IsMarkdownFile: true } tab) return;
        if (!tab.ShowMarkdownPreview) return;

        tab.CaretLine = line;
        tab.CaretColumn = 1;
        tab.ShowMarkdownPreview = false;
    }

    /// <summary>
    /// Esc: 編集モードからプレビューへ戻る。検索オーバーレイが開いている間はそちらのEscを
    /// 優先する（既存機能との衝突回避。SearchOverlay.axaml.cs参照）。<see cref="OnTunnelKeyDown"/>から呼ぶ。
    /// </summary>
    private bool TryHandleMarkdownPreviewEscape(KeyEventArgs e, KeyModifiers mods)
    {
        if (mods != KeyModifiers.None || e.Key != Key.Escape) return false;
        if (_loadedTab is not { Kind: EditorTabKind.Document, IsMarkdownFile: true } tab) return false;
        if (tab.ShowMarkdownPreview || tab.MarkdownPreviewUnavailable) return false;
        if (Search.IsOpen) return false;

        CaptureMarkdownTransitionLine(tab);
        tab.ShowMarkdownPreview = true;
        return e.Handled = true;
    }

    /// <summary>
    /// プロジェクト内の相対リンク（利用者指示の追加要件2）。クリックでGraftのタブとして開く。
    /// 対象が存在しない場合は穏当に知らせる（落ちない）。
    /// </summary>
    private void OnMarkdownRelativeLinkClicked(string relativeUrl)
        => _ = SafeHandler.RunAsync("Markdownの相対リンクを開く", () => OpenMarkdownRelativeLinkAsync(relativeUrl));

    private async Task OpenMarkdownRelativeLinkAsync(string relativeUrl)
    {
        if (_viewModel is null) return;
        if (_loadedTab is not { Kind: EditorTabKind.Document } tab) return;

        var cleanedPath = StripAnchorAndQuery(relativeUrl);
        if (cleanedPath.Length == 0) return;

        var baseDir = Path.GetDirectoryName(tab.Session.FullPath) ?? string.Empty;
        string resolved;
        try
        {
            resolved = Path.GetFullPath(Path.Combine(baseDir, cleanedPath));
        }
        catch (ArgumentException)
        {
            await MarkdownLinkDialogs.ShowMessageAsync(
                "リンクを開けません", $"リンク先のパスを解釈できませんでした。\n\n{relativeUrl}").ConfigureAwait(true);
            return;
        }

        if (!File.Exists(resolved))
        {
            await MarkdownLinkDialogs.ShowMessageAsync(
                "リンク先が見つかりません", $"次のファイルが見つかりませんでした。\n\n{resolved}").ConfigureAwait(true);
            return;
        }

        await _viewModel.OpenFileAsync(resolved, preview: true).ConfigureAwait(true);
    }

    /// <summary>リンクURLからアンカー（#以降）・クエリ（?以降）を取り除き、パス部分だけにする。</summary>
    private static string StripAnchorAndQuery(string url)
    {
        var index = url.IndexOfAny(AnchorOrQueryChars);
        return (index >= 0 ? url[..index] : url).Trim();
    }

    private static readonly char[] AnchorOrQueryChars = { '#', '?' };

    /// <summary>
    /// 外部リンク（<c>https://</c>等、利用者指示の追加要件2）。悪意あるMarkdownファイルへの
    /// 対策として、確認ダイアログを経てからでないと開かない。既存のダイアログ経路
    /// （<see cref="AvaloniaDialogService.ConfirmAsync"/>）とブラウザ起動経路
    /// （<see cref="IExternalLinkLauncher"/>。<see cref="IFileManagerLauncher"/>と同じ
    /// <see cref="PlatformServices"/>経由の抽象化）を再利用する。
    /// </summary>
    private void OnMarkdownExternalLinkClicked(string url)
        => _ = SafeHandler.RunAsync("外部リンクを開く", () => ConfirmAndOpenExternalLinkAsync(url));

    private async Task ConfirmAndOpenExternalLinkAsync(string url)
    {
        var confirmed = await MarkdownLinkDialogs.ConfirmAsync(
            "外部リンクを開きますか？",
            $"Markdownファイル内のリンクから、既定のブラウザで次のURLを開こうとしています。\n\n{url}\n\n信頼できるリンク先の場合のみ「OK」を押してください。")
            .ConfigureAwait(true);
        if (!confirmed) return;

        OpenExternalLinkAction(url);
    }
}
