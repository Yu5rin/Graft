using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Graft.Core;
using Graft.Features;

namespace Graft.Editor;

/// <summary>Gitガターの行種別（仕様書4.7）。削除は行を持たないため三角マークで別途表現する。</summary>
public enum GitGutterKind
{
    /// <summary>追加行（HEADに存在しない行）。</summary>
    Added,
    /// <summary>変更行（HEADの対応行と内容が異なる）。</summary>
    Modified,
}

/// <summary>
/// 4.7 Git変更表示。編集中ファイルの行番号ガターに、HEADとの行差分を色帯（追加=緑・変更=青）と
/// 削除位置の三角マーク（赤）で表示する。<see cref="AbstractMargin"/>として実装し、
/// <see cref="TextEditor"/>を直接受け取って動作する独立クラス。<see cref="TextArea.LeftMargins"/>への
/// 追加、タブ切替時の<see cref="SetTarget"/>呼び出し、保存後の<see cref="RefreshAsync"/>呼び出しは
/// 統合担当が行う（このクラス自身はエディタへ自己接続しない）。
/// 行差分の算出は独自実装を持たず<see cref="Core.DiffBuilder"/>（DiffPlex）をそのまま再利用し、
/// 差分結果を追加/変更/削除の3種へ分類する処理のみをこのクラスで行う。18章の性能要件により、
/// HEAD内容の取得（<c>git show</c>）と差分計算はファイル保存のたびに1回だけ非同期で行い、
/// UIスレッドを塞がない。プロジェクトがGit管理外、またはgit未検出の場合は速やかに諦め、
/// 何も描画しない。
/// WPF版（AvalonEdit）からの移植。描画は<c>OnRender(DrawingContext)</c>から
/// <see cref="Visual.Render(DrawingContext)"/>のオーバーライドへ、<c>RenderSize</c>は
/// <see cref="Visual.Bounds"/>のSizeへ、<c>System.Windows.Automation.AutomationProperties</c>は
/// <see cref="Avalonia.Automation.AutomationProperties"/>へ、<c>ToolTipService</c>は
/// <see cref="ToolTip"/>へ、<c>MouseEventArgs</c>は<see cref="PointerEventArgs"/>へそれぞれ
/// 差し替える。Brush/Pen/GeometryのFreeze()はAvaloniaに対応物が無いため呼び出さない。
/// </summary>
public sealed class GitGutterProvider : AbstractMargin, IDisposable
{
    private const double MarginWidth = 6.0;
    private const double BandWidth = 3.0;

    private readonly TextEditor _editor;
    private readonly GitIntegration _git;
    private string? _projectRoot;
    private string? _relativePath;
    private bool _enabled = true;
    private bool _disposed;
    private CancellationTokenSource? _refreshCts;
    private Dictionary<int, GitGutterKind> _bands = new();
    private HashSet<int> _deletionMarks = new();

    public GitGutterProvider(TextEditor editor, GitIntegration gitIntegration)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _git = gitIntegration ?? throw new ArgumentNullException(nameof(gitIntegration));
        AutomationProperties.SetName(this, "Gitガター（HEADとの差分表示）");
    }

    /// <summary>
    /// 対象ファイルを切り替える（タブのオープン・切替のたび呼ぶ）。表示は即座にクリアされ、
    /// 続けて<see cref="RefreshAsync"/>を呼ぶまでは何も表示しない。
    /// </summary>
    public void SetTarget(string? projectRoot, string? relativePath)
    {
        _refreshCts?.Cancel();
        _projectRoot = projectRoot;
        _relativePath = relativePath;
        ClearMarks();
    }

    /// <summary>15章 <c>editor.gitGutter</c> 設定の反映。</summary>
    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        if (!enabled) ClearMarks();
        else InvalidateVisual();
    }

    /// <summary>
    /// HEADとの行差分を再計算する（保存時に呼ぶ）。プロジェクトがGit管理外・git未検出の場合は
    /// 表示をクリアして速やかに終える。呼び出し中に次の呼び出しが来た場合は前回分をキャンセルする。
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (!_enabled || _projectRoot is null || _relativePath is null)
        {
            ClearMarks();
            return;
        }

        _refreshCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _refreshCts = cts;

        GitHeadContent head;
        try
        {
            head = await _git.GetHeadFileContentAsync(_projectRoot, _relativePath, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cts.IsCancellationRequested) return;

        if (!head.IsRepository)
        {
            ClearMarks();
            return;
        }

        ApplyDiff(head.Content, _editor.Document.Text);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshCts?.Cancel();
    }

    protected override Size MeasureOverride(Size availableSize) => new(MarginWidth, 0);

    public override void Render(DrawingContext drawingContext)
    {
        var renderSize = Bounds.Size;
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(renderSize));

        var textView = TextView;
        if (!_enabled || textView is null || !textView.VisualLinesValid) return;

        foreach (var line in textView.VisualLines)
        {
            var lineNumber = line.FirstDocumentLine.LineNumber;
            var top = line.VisualTop - textView.VerticalOffset;
            DrawBand(drawingContext, lineNumber, top, line.Height);
            DrawDeletionMark(drawingContext, lineNumber, top);
        }
    }

    private void DrawBand(DrawingContext dc, int lineNumber, double top, double height)
    {
        if (!_bands.TryGetValue(lineNumber, out var kind)) return;
        var brush = ResolveBrush(kind == GitGutterKind.Added ? "GutterAdd" : "GutterMod");
        if (brush is null) return;
        dc.DrawRectangle(brush, null, new Rect(0, top, BandWidth, height));
    }

    private void DrawDeletionMark(DrawingContext dc, int lineNumber, double top)
    {
        if (!_deletionMarks.Contains(lineNumber)) return;
        var brush = ResolveBrush("GutterDel");
        if (brush is null) return;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(0, top - 3), true);
            ctx.LineTo(new Point(0, top + 3));
            ctx.LineTo(new Point(MarginWidth, top));
            ctx.EndFigure(true);
        }
        dc.DrawGeometry(brush, null, geometry);
    }

    protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
    {
        if (oldTextView is not null)
        {
            oldTextView.VisualLinesChanged -= OnRedrawRequested;
            oldTextView.ScrollOffsetChanged -= OnRedrawRequested;
        }
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView is not null)
        {
            newTextView.VisualLinesChanged += OnRedrawRequested;
            newTextView.ScrollOffsetChanged += OnRedrawRequested;
        }
        InvalidateVisual();
    }

    private void OnRedrawRequested(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>9.4: 色帯は色のみに依存しないよう、ホバー位置の種別をツールチップで日本語表示する。</summary>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        ToolTip.SetTip(this, ResolveTooltipText(e.GetPosition(this)));
    }

    private string? ResolveTooltipText(Point position)
    {
        var textView = TextView;
        if (textView is null) return null;

        var docLine = textView.GetDocumentLineByVisualTop(position.Y + textView.VerticalOffset);
        if (docLine is null) return null;

        if (_deletionMarks.Contains(docLine.LineNumber)) return "削除（HEADに存在した行が削除されました）";
        if (_bands.TryGetValue(docLine.LineNumber, out var kind))
        {
            return kind == GitGutterKind.Added ? "追加（HEADに存在しない行です）" : "変更（HEADの内容と異なります）";
        }
        return null;
    }

    private void ApplyDiff(string? headContent, string currentText)
    {
        var model = DiffBuilder.BuildFull(_relativePath ?? string.Empty, headContent, currentText);
        var lines = model.Hunks.SelectMany(h => h.Lines).ToList();
        var (bands, deletionMarks) = BuildGutterState(lines);
        _bands = bands;
        _deletionMarks = deletionMarks;
        InvalidateVisual();
    }

    /// <summary>
    /// DiffPlex（<see cref="Core.DiffBuilder"/>）が返した行差分を、連続する削除行・追加行の並びごとに
    /// 追加/変更/削除の3種へ分類する。差分アルゴリズム自体はDiffBuilderの結果をそのまま使い、
    /// ここではガター表示用の分類のみを行う。
    /// </summary>
    private static (Dictionary<int, GitGutterKind> Bands, HashSet<int> DeletionMarks) BuildGutterState(
        IReadOnlyList<DiffLine> lines)
    {
        var bands = new Dictionary<int, GitGutterKind>();
        var deletionMarks = new HashSet<int>();
        var i = 0;
        while (i < lines.Count)
        {
            if (lines[i].Kind != DiffLineKind.Removed) { i++; continue; }
            i = ClassifyChangeBlock(lines, i, bands, deletionMarks);
        }
        return (bands, deletionMarks);
    }

    private static int ClassifyChangeBlock(
        IReadOnlyList<DiffLine> lines, int start, Dictionary<int, GitGutterKind> bands, HashSet<int> deletionMarks)
    {
        var removedStart = start;
        var i = start;
        while (i < lines.Count && lines[i].Kind == DiffLineKind.Removed) i++;
        var removedCount = i - removedStart;

        var addedStart = i;
        while (i < lines.Count && lines[i].Kind == DiffLineKind.Added) i++;
        var addedCount = i - addedStart;

        for (var k = 0; k < addedCount; k++)
        {
            if (lines[addedStart + k].NewLine is not int newLine) continue;
            bands[newLine] = k < removedCount ? GitGutterKind.Modified : GitGutterKind.Added;
        }

        if (removedCount > addedCount)
        {
            var anchor = ResolveDeletionAnchor(lines, addedStart, addedCount, removedStart, i);
            if (anchor is int a) deletionMarks.Add(a);
        }

        return i;
    }

    /// <summary>
    /// 純粋な削除（対応する追加行がない）の位置を、新ファイル側で削除の直後にあたる行の行番号に
    /// 割り当てる。ファイル末尾での削除など、直後の行が存在しない場合は直前の行に割り当てる。
    /// </summary>
    private static int? ResolveDeletionAnchor(
        IReadOnlyList<DiffLine> lines, int addedStart, int addedCount, int removedStart, int nextIndex)
    {
        if (addedCount > 0) return lines[addedStart].NewLine;

        for (var i = nextIndex; i < lines.Count; i++)
        {
            if (lines[i].NewLine is int after) return after;
        }
        for (var i = removedStart - 1; i >= 0; i--)
        {
            if (lines[i].NewLine is int before) return before;
        }
        return null;
    }

    private void ClearMarks()
    {
        if (_bands.Count == 0 && _deletionMarks.Count == 0) return;
        _bands = new Dictionary<int, GitGutterKind>();
        _deletionMarks = new HashSet<int>();
        InvalidateVisual();
    }

    private static IBrush? ResolveBrush(string resourceKey)
        => Application.Current is { } app && app.TryFindResource(resourceKey, null, out var value) ? value as IBrush : null;
}
