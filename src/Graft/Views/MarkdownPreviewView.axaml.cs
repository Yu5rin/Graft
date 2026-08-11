using Avalonia.Controls;

namespace Graft.Views;

/// <summary>
/// Markdownプレビュー機能（利用者指示）: .mdファイルのエディタ内プレビュー表示。
/// <see cref="ManualMarkdownRenderer"/>（<see cref="ManualWindow"/>と共用のパーサ。
/// フォークせず拡張した経緯は同クラスのコメント参照）でブロックを組み立て、
/// <see cref="ContentPanel"/>へ流し込む。<see cref="Views.EditorPane"/>がタブごとの
/// モード切替・スクロール位置の受け渡しを仲介する（EditorPane.axaml.cs参照）。
/// </summary>
public partial class MarkdownPreviewView : UserControl
{
    private IReadOnlyList<ManualMarkdownRenderer.RenderedBlock> _blocks = Array.Empty<ManualMarkdownRenderer.RenderedBlock>();
    private IReadOnlyDictionary<string, Control> _anchors = new Dictionary<string, Control>();

    public MarkdownPreviewView()
    {
        InitializeComponent();
    }

    /// <summary>ブロックがダブルクリックされたときに、その開始行（1始まり）を渡して発火する。</summary>
    public event Action<int>? BlockDoubleClicked;

    /// <summary>
    /// Markdown全文から本文を組み立て直す。呼び出し側（EditorPane）は編集中のバッファ
    /// （<c>DocumentSession.Document.Text</c>）をそのまま渡すこと。ディスクを読み直さない
    /// （利用者指示の追加要件4: 未保存の編集を反映するため）。
    /// </summary>
    public void Render(
        string markdown,
        Action<string> onAnchorClicked,
        Action<string>? onRelativeLinkClicked,
        Action<string>? onExternalLinkClicked)
    {
        ContentPanel.Children.Clear();

        var result = ManualMarkdownRenderer.Render(
            markdown, onAnchorClicked, onRelativeLinkClicked, onExternalLinkClicked,
            onBlockDoubleClicked: line => BlockDoubleClicked?.Invoke(line));

        _blocks = result.Blocks;
        _anchors = result.Anchors;
        foreach (var block in _blocks)
        {
            ContentPanel.Children.Add(block.Control);
        }
    }

    /// <summary>目次・段落中の同一文書内アンカーリンクから該当見出しへスクロールする。</summary>
    public void JumpToAnchor(string anchor)
    {
        if (_anchors.TryGetValue(anchor, out var target)) target.BringIntoView();
    }

    /// <summary>
    /// 現在の縦スクロール位置に最も近い（かつそれより手前の）ブロックの開始行を返す。
    /// モード切替時にエディタ側へおおよそのスクロール位置を引き継ぐために使う
    /// （利用者指示: 切り替えてもスクロール位置を保つ）。ブロックが無ければ1を返す。
    /// </summary>
    public int GetTopVisibleLine()
    {
        if (_blocks.Count == 0) return 1;

        var offset = PreviewScroll.Offset.Y;
        var best = _blocks[0];
        foreach (var block in _blocks)
        {
            // 各ブロックのBounds.YはContentPanel（ScrollViewerの直接の子）からの相対位置で、
            // PreviewScroll.Offset.Yと同じ座標系のため直接比較できる。
            if (block.Control.Bounds.Y <= offset + 1) best = block;
            else break;
        }
        return best.StartLine;
    }

    /// <summary>
    /// 指定行に対応するブロック（その行以下で最大の開始行を持つブロック。無ければ先頭）へ
    /// スクロールする。編集モードからの復帰・タブ切替時の位置合わせに使う。
    /// </summary>
    public void ScrollToLine(int line)
    {
        if (_blocks.Count == 0) return;

        var target = _blocks[0];
        foreach (var block in _blocks)
        {
            if (block.StartLine <= line) target = block;
            else break;
        }
        target.Control.BringIntoView();
    }
}
