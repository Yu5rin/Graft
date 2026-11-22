using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Graft.Platform;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// コンテキスト収集ウィンドウ（仕様書10章）。DataContextには
/// <see cref="ContextCollectViewModel"/>を受け取る。v2.0のWPF版からの移植（19章 L3）。
/// </summary>
public partial class ContextCollectWindow : Window
{
    /// <summary>headlessテスト・デザイナ用の引数なしコンストラクタ。</summary>
    public ContextCollectWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
        // 細かいユーザビリティ改善5: 開いた直後の初期フォーカスを最初の操作対象（収集モード）へ当てる。
        // headlessテスト・デザイナ用のこのコンストラクタでも効くよう、DataContextを必要としない
        // ここに置く（DataContextを持つコンストラクタ側にだけ置くと、テストで既定コンストラクタを
        // 直接使った場合にフォーカスが当たらなくなる）。
        Loaded += (_, _) => ModeComboBox.Focus();

        // ファイル選択タブの割り付け（広い窓＝ファイル一覧の右にブックの情報／狭い窓＝「ブックの情報」タブ）は、
        // 窓の幅と、ブックの情報の枠が出ているかで決まる。どちらが変わっても作り直す。
        PropertyChanged += (_, e) =>
        {
            if (e.Property == ClientSizeProperty) UpdateFileSelectLayout();
        };
        WorkbookInfoRow.PropertyChanged += OnWorkbookRowVisibilityChanged;
        RibbonRow.PropertyChanged += OnWorkbookRowVisibilityChanged;
        UpdateFileSelectLayout();
    }

    /// <summary>
    /// ブックの情報の側を、ファイル一覧の右に並べる最小の窓の幅。
    /// 860 = 左右の余白 32 + ファイル一覧の最小 280 + 仕切り 6 + ブックの情報の最小 320（シート名 180 + 見せ方の選択 140）に、
    /// 余裕を持たせた値。これ未満では、1 つの縦の列にファイル一覧とシート一覧の 2 つを積むことになり、
    /// 760×720 の試作でシートが 1〜2 行・最小の窓でファイル一覧が 0 行になって、どう高さを配ってもつぶれた。
    /// そのため、狭い窓ではタブに分ける。
    /// </summary>
    private const double SideBySideMinWidth = 860;

    /// <summary>いまの割り付け。-1 = まだ決めていない、0 = ブックの情報なし、1 = 左右に並べる、2 = 「ブックの情報」タブ。</summary>
    private int _fileSelectLayout = -1;

    private void OnWorkbookRowVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty) UpdateFileSelectLayout();
    }

    /// <summary>
    /// ファイル選択タブの割り付けを決め直す。
    /// ・ブックの情報の枠もリボンの枠も出ていない（通常のプロジェクト）: ファイル一覧だけ（全幅）。
    /// ・出ていて、窓が広い（<see cref="SideBySideMinWidth"/> 以上）: 左にファイル一覧、右にブックの情報。
    /// ・出ていて、窓が狭い: ブックの情報の部品を「ブックの情報」タブへ移し、ファイル選択タブの先頭に、
    ///   値が渡る注意などの要約を 1 行だけ残す（仕様書_ブックの情報と依頼の観点 5.3 の常に出す注意を、
    ///   ファイル選択タブを見ているときも守る）。
    /// 同じ部品を 2 つ作らず、親を付け替える（同じ見た目の部品が 2 つあることによる不具合を過去に踏んでいるため）。
    /// </summary>
    private void UpdateFileSelectLayout()
    {
        var hasWorkbook = WorkbookInfoRow.IsVisible || RibbonRow.IsVisible;
        // 部品そのものの表示は、2 つの枠のどちらかが出ているとき（どちらも出ていなければ、余白だけが残らないよう隠す）。
        WorkbookColumn.IsVisible = hasWorkbook;

        var layout = !hasWorkbook ? 0 : ClientSize.Width >= SideBySideMinWidth ? 1 : 2;
        if (_fileSelectLayout == layout) return;
        _fileSelectLayout = layout;

        // 【親の付け替えで、フォーカスと入力途中の値を失わない】 部品の親を替えると、いったん画面の木から外れるので、
        // フォーカスのある部品（範囲の入力欄・行数の欄・リボンの行など）はフォーカスを失う。放っておくと、
        //  ① キーボード利用者はフォーカスの位置を失い、Tab が窓の先頭から始まる、
        //  ② 入力欄の LostFocus が「入力の確定」を走らせ、打っている途中の範囲（「B2:」など）が誤りとして確定される、
        //  ③ 狭い窓へ移ると、入力していた欄が、選ばれていない「ブックの情報」タブの中へ消える（実測: 入力欄が窓の中=False）。
        // そこで、付け替えの前にフォーカスのある部品を覚え、付け替えの間は入力の確定を止め（下書きのまま残す）、付け替えたあとに戻す。
        var focusedBefore = FocusManager?.GetFocusedElement() as Control;
        FocusKey? restoreFocus = focusedBefore is not null
            && (ReferenceEquals(focusedBefore, WorkbookColumn) || focusedBefore.GetVisualAncestors().Contains(WorkbookColumn))
            ? FocusKey.Of(focusedBefore)
            : null;

        var host = layout == 2 ? (Panel)WorkbookTabHost : FileSelectGrid;
        if (!ReferenceEquals(WorkbookColumn.Parent, host))
        {
            var previousSuppress = SheetViewDraftBehavior.SuppressCommit;
            SheetViewDraftBehavior.SuppressCommit = restoreFocus is not null;
            try
            {
                (WorkbookColumn.Parent as Panel)?.Children.Remove(WorkbookColumn);
                host.Children.Add(WorkbookColumn);
            }
            finally
            {
                SheetViewDraftBehavior.SuppressCommit = previousSuppress;
            }
        }

        WorkbookTab.IsVisible = layout == 2;
        WorkbookSummaryRow.IsVisible = layout == 2;
        WorkbookSplitter.IsVisible = layout == 1;
        // 見ていたタブが消えるときは、ファイル選択へ戻す（消えたタブを選んだままにしない）。
        if (layout != 2 && ReferenceEquals(MainTabs.SelectedItem, WorkbookTab)) MainTabs.SelectedIndex = 0;

        if (layout == 1)
        {
            // ファイル一覧 5 : ブックの情報 4（それより広げた分は両方へ配る）。仕切りで幅を変えられる。
            FileSelectGrid.ColumnDefinitions = new ColumnDefinitions("5*,Auto,4*");
            FileSelectGrid.ColumnDefinitions[0].MinWidth = 280;
            FileSelectGrid.ColumnDefinitions[2].MinWidth = 320;
            Grid.SetColumn(WorkbookSplitter, 1);
            Grid.SetColumn(WorkbookColumn, 2);
        }
        else
        {
            FileSelectGrid.ColumnDefinitions = new ColumnDefinitions("*");
            Grid.SetColumn(WorkbookSplitter, 0);
            Grid.SetColumn(WorkbookColumn, 0);
        }

        if (restoreFocus is { } key) RestoreFocusAfterLayoutChange(key, layout);
    }

    /// <summary>フォーカスを戻す依頼の番号。新しい付け替えが起きたら進め、古い依頼が遅れて走っても何もしないようにする。</summary>
    private int _focusRestoreToken;

    /// <summary>
    /// フォーカスのあった部品の「見つけ直すための手がかり」。
    /// 【部品そのものは覚えない】 シート一覧のような一覧の行は、親を付け替えて画面の木に付け直すと、行の部品が
    /// <b>作り直される</b>（実測: 付け替え後、同じ範囲の入力欄が別のインスタンスになり、元のものは木に付かないまま）。
    /// そこで、種類・束縛先（<see cref="StyledElement.DataContext"/>。行の ViewModel）・名前・読み上げの名前で、作り直されたあとの
    /// 同じ部品を見つける。範囲の欄と行数の欄は、同じ行の ViewModel を持つが、読み上げの名前が違う（シート名つき）ので区別できる。
    /// </summary>
    private readonly record struct FocusKey(Type Type, object? DataContext, string? Name, string? AutomationName, int Caret, int SelectionStart, int SelectionEnd)
    {
        public static FocusKey Of(Control control)
        {
            var text = control as TextBox;
            return new FocusKey(
                control.GetType(), control.DataContext, control.Name, AutomationProperties.GetName(control),
                text?.CaretIndex ?? 0, text?.SelectionStart ?? 0, text?.SelectionEnd ?? 0);
        }

        public bool Matches(Control control)
            => control.GetType() == Type
               && ReferenceEquals(control.DataContext, DataContext)
               && control.Name == Name
               && AutomationProperties.GetName(control) == AutomationName;
    }

    /// <summary>
    /// 付け替えたあとに、フォーカスを元の部品へ戻す。狭い窓（タブ）へ移ったときは、先に「ブックの情報」タブを選ぶ
    /// （選ばれていないタブの中の部品は画面の木に付いておらず、フォーカスを受けられない）。
    /// <para>
    /// 【配置のたびに、できるまで試す】 タブを選んだ直後は、タブの中身がまだ画面の木に付いておらず、一覧の行の部品も
    /// 作り直される途中なので、その場では見つからない・フォーカスを受けられない。配置が終わるたびに（<see cref="Layoutable.LayoutUpdated"/>）
    /// 見つけ直して戻し、戻せたら（または利用者が別の部品へ移った・新しい付け替えが起きたら）やめる。無限には待たず、回数の上限を持つ。
    /// 戻している間は、入力の確定を止めておく（フォーカスの出入りで下書きが確定されないように）。
    /// </para>
    /// </summary>
    private void RestoreFocusAfterLayoutChange(FocusKey key, int layout)
    {
        var token = ++_focusRestoreToken;
        if (layout == 2) MainTabs.SelectedItem = WorkbookTab;

        // 戻せたか、もう戻さなくてよい（利用者が別の部品へ移った・新しい付け替えが起きた）なら true。まだなら false。
        bool TryRestore()
        {
            if (token != _focusRestoreToken) return true;
            var current = FocusManager?.GetFocusedElement();
            if (current is not null && !(current is TabItem) && !(current is Control c && key.Matches(c))) return true;

            var target = WorkbookColumn.GetVisualDescendants().OfType<Control>()
                .FirstOrDefault(c => key.Matches(c) && c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled);
            if (target is null) return false;

            var previousSuppress = SheetViewDraftBehavior.SuppressCommit;
            SheetViewDraftBehavior.SuppressCommit = true;
            try
            {
                if (!target.Focus()) return false;
                if (target is TextBox text)
                {
                    // 打っていた位置・選択も戻す（作り直された入力欄は、先頭に戻っているため）。
                    text.CaretIndex = key.Caret;
                    text.SelectionStart = key.SelectionStart;
                    text.SelectionEnd = key.SelectionEnd;
                }

                return true;
            }
            finally
            {
                SheetViewDraftBehavior.SuppressCommit = previousSuppress;
            }
        }

        if (TryRestore()) return;

        var attempts = 0;
        void OnLayoutUpdated(object? sender, EventArgs e)
        {
            if (++attempts > 30 || TryRestore()) LayoutUpdated -= OnLayoutUpdated;
        }

        LayoutUpdated += OnLayoutUpdated;
    }

    /// <param name="viewModel">コンテキスト収集のViewModel。</param>
    /// <param name="requestedPaths">
    /// AIが「このファイルも見せて」（E710）と求めたパス。空でなければ、初回の走査が終わったあとに
    /// そのファイルを「内容も出す」に設定し、結果（見つからない・除外されている等）を
    /// ステータス欄に出す。走査の前に設定すると、走査が選択状態を作り直して失われるため、
    /// 必ず<see cref="ContextCollectViewModel.InitializeAsync"/>の後に行う。
    /// </param>
    public ContextCollectWindow(ContextCollectViewModel viewModel, IReadOnlyList<string>? requestedPaths = null) : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        // Excel VBA 連携（仕様書_VBA連携 7.2）: 走査の前の自動 Pull で衝突したときのダイアログは、この窓がモーダルで
        // 開いている間、この窓を親にして出す。メインウィンドウを親にすると、モーダルの裏に回って操作できない。
        // 窓が閉じたら外す（窓を開かない経路——依頼文コピー——では、メインウィンドウが親の既定に戻る）。
        viewModel.ShowVbaConflictAsync = async request =>
            await new VbaConflictWindow(viewModel.CreateVbaConflictViewModel(request)).ShowAndResolveAsync(this).ConfigureAwait(true);
        // 取り込み前の未保存の確認など、ほかのダイアログも同じ理由で、この窓が開いている間はこの窓を親にする。
        Opened += (_, _) => AvaloniaDialogService.PreferredOwner = this;
        // 入力欄（ブックの情報の範囲・行数）に打っていた途中の文字を、窓を閉じる直前に確定する。×・Esc・「閉じる」のどれでも、
        // フォーカスが外れる前に閉じることがあり、LostFocus だけに頼ると、打った内容が保存・反映されないまま消える。
        Closing += (_, _) => _ = SafeHandler.RunAsync("入力の確定", viewModel.CommitPendingInputsAsync);
        Closed += (_, _) =>
        {
            viewModel.ShowVbaConflictAsync = null;
            if (ReferenceEquals(AvaloniaDialogService.PreferredOwner, this)) AvaloniaDialogService.PreferredOwner = null;
        };
        Loaded += async (_, _) =>
        {
            await SafeHandler.RunAsync("コンテキスト収集の初期化", () => viewModel.InitializeAsync())
                .ConfigureAwait(true);
            if (requestedPaths is { Count: > 0 })
            {
                await SafeHandler.RunAsync("要求されたファイルの反映", () => viewModel.ApplyRequestedFilesAsync(requestedPaths))
                    .ConfigureAwait(true);
            }
        };
    }

    /// <summary>
    /// 窓全体のキー操作（トンネル＝子より先に受ける）。
    ///
    /// 【Esc】従来どおり窓を閉じる。ただしファイル名の絞り込み欄にフォーカスがあり、中身が
    /// あるときだけは、窓を閉じずに欄を空にする（絞り込みを解いてツリーへ戻す）。
    /// 入力欄でEscを押す利用者の意図は「入力を取り消す」であり、絞り込み途中のつもりで
    /// 押して窓ごと閉じる（選択途中の状態を失ったように感じる）のを避けるため。
    /// 欄が空のとき、または別の部品にフォーカスがあるときは、これまでと変わらず窓を閉じる
    /// （Escで必ず閉じられる、という他のダイアログとの一貫性を保つ）。
    /// 絞り込み欄の中身の有無は、ViewModelではなく欄自身のTextで見る。DataContextを持たない
    /// 既定コンストラクタ（デザイナ・一部のテスト）でも同じ挙動になるようにするため。
    ///
    /// 【Ctrl+F】絞り込み欄へ移る（この窓には、ほかにCtrl+Fを使う操作が無い。エディタの検索の
    /// Ctrl+Fは別のウィンドウの話で、このモーダルの中では衝突しない）。プレビュータブを
    /// 見ているときも、ファイル選択タブへ切り替えてから移る。すでに欄にいるときは、
    /// 全選択して打ち直しやすくする（多くのアプリのCtrl+Fの挙動に合わせる）。
    /// </summary>
    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (FileFilterBox.IsKeyboardFocusWithin && !string.IsNullOrEmpty(FileFilterBox.Text))
            {
                FileFilterBox.Text = string.Empty;
                e.Handled = true;
                return;
            }

            Close();
            return;
        }

        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            FocusFileFilter();
            e.Handled = true;
        }
    }

    /// <summary>ファイル選択タブを表示して、絞り込み欄へフォーカスを移す。</summary>
    private void FocusFileFilter()
    {
        MainTabs.SelectedIndex = 0;
        FileFilterBox.Focus();
        FileFilterBox.SelectAll();
    }

    /// <summary>
    /// 実機で確認された指摘7: 設定・キュー・取扱説明書にはある「閉じる」ボタンがこのウィンドウ
    /// にだけ無く、マウスだけの利用者にはタイトルバーの×しか到達手段が無かった。
    /// </summary>
    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
