namespace Graft.ViewModels;

/// <summary>
/// <see cref="DiffViewModel"/> の分割ファイル（1ファイル400行上限のため）。
/// 仕様書4.8「diffからジャンプ」: diff表示の行をダブルクリックした際、該当ファイルの該当行を
/// エディタで開くための要求を発火する。View側（<see cref="Views.EditorPane"/>）が
/// <see cref="RequestJump"/> を呼び、ShellViewModelが<see cref="JumpRequested"/>を
/// EditorPaneViewModel.OpenFileAsyncへ結ぶ。
/// </summary>
public sealed partial class DiffViewModel
{
    /// <summary>4.8: diff行のダブルクリックで発火する。変更後の行番号を優先する。</summary>
    public event EventHandler<(string RelativePath, int Line)>? JumpRequested;

    /// <summary>4.8: diff行のダブルクリックから呼ぶ。省略行では何もしない。</summary>
    public void RequestJump(DiffLineViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_plan is null || row.IsOmitted) return;

        var line = ResolveLine(row.Right) ?? ResolveLine(row.Left);
        if (line is int l) JumpRequested?.Invoke(this, (_plan.Path, l));
    }

    // 変更後（NewLineText）を優先し、無ければ変更前（OldLineText）を使う（仕様書4.8）。
    private static int? ResolveLine(DiffCellViewModel? cell)
    {
        if (cell is null || ReferenceEquals(cell, DiffCellViewModel.Blank)) return null;
        if (int.TryParse(cell.NewLineText, out var n)) return n;
        if (int.TryParse(cell.OldLineText, out var o)) return o;
        return null;
    }
}
