using System.Windows.Input;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="EditorPaneViewModel"/> の分割ファイル（1ファイル400行上限のため）。
///
/// 検討書「折りたたみの機能追加」(b) 折りたたみコマンド3種（レベル1〜5・すべてのコメント
/// ブロック・再帰的）の、View（<see cref="Views.EditorPane"/>）への橋渡しを担う。
/// 折りたたみの実体（<see cref="Editor.FoldingSupport"/>）はViewの<c>Editor</c>
/// （AvaloniaEditの<c>TextEditor</c>）に紐づいており、ViewModel層から直接参照できない
/// （他の<c>editor.*</c>設定と同じ制約）。そこで既存の<see cref="FontSizeChangeCommitted"/>と
/// 同じ「ViewModel→Viewのイベント」パターンを踏襲し、コマンド実行の要求だけをここから発信する。
///
/// コマンドパレット（<c>ShellViewModel.CommandPalette.cs</c>）・キーボードショートカット
/// （<c>EditorPane.axaml.cs</c>のOnTunnelKeyDown）のどちらも、必ずこの<see cref="ICommand"/>群
/// 経由の同じ1本の経路を通る（挙動を二重に持たせない。<see cref="ResolveDropIndex"/>等と同じ
/// 「実座標に依存する部分を1箇所へ切り出す」考え方をコマンド分岐へ適用したもの）。
/// </summary>
public sealed partial class EditorPaneViewModel
{
    /// <summary>
    /// 折りたたみコマンドが要求されたことの通知（コマンドパレット・ショートカットのどちらか）。
    /// <see cref="Views.EditorPane"/>が購読し、対象タブがドキュメントタブであることを確認した
    /// うえで、実際の<see cref="Editor.FoldingSupport"/>呼び出しを行う。
    /// </summary>
    public event EventHandler<FoldCommandKind>? FoldCommandRequested;

    public ICommand FoldLevel1Command { get; private set; } = null!;
    public ICommand FoldLevel2Command { get; private set; } = null!;
    public ICommand FoldLevel3Command { get; private set; } = null!;
    public ICommand FoldLevel4Command { get; private set; } = null!;
    public ICommand FoldLevel5Command { get; private set; } = null!;
    public ICommand FoldAllCommentsCommand { get; private set; } = null!;
    public ICommand FoldRecursiveCommand { get; private set; } = null!;

    /// <summary>コンストラクタ終端から呼ぶ（InitializeTabActionCommands等と同じ配置規則）。</summary>
    private void InitializeFoldCommands()
    {
        FoldLevel1Command = new RelayCommand(() => RequestFoldCommand(FoldCommandKind.Level1));
        FoldLevel2Command = new RelayCommand(() => RequestFoldCommand(FoldCommandKind.Level2));
        FoldLevel3Command = new RelayCommand(() => RequestFoldCommand(FoldCommandKind.Level3));
        FoldLevel4Command = new RelayCommand(() => RequestFoldCommand(FoldCommandKind.Level4));
        FoldLevel5Command = new RelayCommand(() => RequestFoldCommand(FoldCommandKind.Level5));
        FoldAllCommentsCommand = new RelayCommand(() => RequestFoldCommand(FoldCommandKind.AllComments));
        FoldRecursiveCommand = new RelayCommand(() => RequestFoldCommand(FoldCommandKind.Recursive));
    }

    private void RequestFoldCommand(FoldCommandKind kind) => FoldCommandRequested?.Invoke(this, kind);
}

/// <summary>折りたたみコマンドの種類（検討書「折りたたみの機能追加」(b)）。</summary>
public enum FoldCommandKind
{
    /// <summary>レベル1（最も外側）だけを折りたたみ、それ以外は展開する。</summary>
    Level1,
    /// <summary>レベル2だけを折りたたみ、それ以外は展開する。</summary>
    Level2,
    /// <summary>レベル3だけを折りたたみ、それ以外は展開する。</summary>
    Level3,
    /// <summary>レベル4だけを折りたたみ、それ以外は展開する。</summary>
    Level4,
    /// <summary>レベル5だけを折りたたみ、それ以外は展開する。</summary>
    Level5,
    /// <summary>すべてのコメントブロック（2行以上連続するコメント専用行）を折りたたむ。</summary>
    AllComments,
    /// <summary>カーソル位置の範囲を、内側も含めて再帰的に折りたたむ。</summary>
    Recursive,
}
