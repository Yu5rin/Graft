using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Graft.Features;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// <see cref="ShellWindow"/> の分割ファイル（1ファイル400行上限のため）。
///
/// 画面上のチュートリアル（コーチマーク方式）を担う。利用者からの指摘
/// 「サンプルプロジェクトは素晴らしい試みですが、接ぎ木が体験できないので、ソフトの中核を
/// 体験できない」への対応で、実際のシェル画面の上に半透明のオーバーレイ（<see cref="TutorialOverlay"/>）を
/// 敷き、対象のコントロールだけを明るく見せて、その近くに吹き出しで説明を出しながら、
/// 「サンプルを用意 → 解析 → 差分確認 → 適用 → 履歴確認 → 復元」を実際に1回体験させる。
///
/// 【実データを一切触らない設計】
/// - サンプルは<see cref="OnboardingSample"/>が一時フォルダへ生成する（プロジェクトフォルダ・
///   パッチファイルとも<c>Path.GetTempPath()</c>配下のみ）。
/// - チュートリアル開始前に選択していたプロジェクト（<see cref="_tutorialPreviousProjectId"/>）は、
///   終了時（正常終了・Esc・「終了」ボタンのいずれでも）に必ず選び直す。
/// - サンプルの登録（projects.jsonへのエントリ）は、終了時に必ず取り除く（判断・理由は
///   <see cref="FinishTutorialAsync"/>のコメント参照）。
///
/// 【実際の操作の実行方法】
/// 各ステップの「適用する」「元に戻す」は、ツールバー・接ぎ木パネル・履歴ペインの実ボタンが
/// 束ねているのと全く同じ<see cref="System.Windows.Input.ICommand"/>（<c>Graft.ApplyCommand</c>・
/// <c>Graft.History.RestoreCommand</c>）をこのクラスから直接実行する。確認ダイアログ・
/// バックアップ・履歴記録は実ボタンを押した場合と完全に同一の経路を通るため、「実際に適用
/// させる」「実際に1回戻させる」という要件を、ボタンを複製せずに満たせる
/// （<see cref="ExecuteAndWaitAsync"/>はGraft.UiTests/ScenarioTests.csのExecuteAsyncと同じ
/// 「AsyncRelayCommand.IsExecutingを見て完了を待つ」作法）。
///
/// 【対象コントロールを明るく見せるだけで、クリックはさせない設計】
/// <see cref="TutorialOverlay"/>の暗幕（ScrimPath）は対象の位置に視覚的な「穴」を空けるが、
/// 全面がヒットテスト対象（クリック等を奪う）のままのため、対象コントロール自体を
/// 直接クリックすることはできない。実際の操作は必ず吹き出し内のボタン（「適用する」
/// 「元に戻す」等）から行う設計にした。理由: 各ステップの完了判定（次のステップへ進んで
/// よいかどうか）を、対象コントロールへの任意のクリック操作から検出するのは
/// （ダイアログの多段化・非同期処理のタイミング等により）壊れやすく、途中で中断された
/// ときに中途半端な状態が残るリスクが高い。吹き出しのボタンからのみ実行する設計であれば、
/// 「今どの操作を実行中か」をこのクラス自身が把握できるため、Esc/「終了」による中断も
/// 安全に行える。
/// </summary>
public partial class ShellWindow
{
    private const int TutorialStepCount = 7;

    private bool _tutorialActive;
    private int _tutorialStep;
    private bool _tutorialSampleReady;
    private bool _tutorialPatchAnalyzed;
    private string? _tutorialSampleRoot;
    private string? _tutorialPatchFilePath;
    private string? _tutorialSampleProjectId;
    private string? _tutorialPreviousProjectId;

    /// <summary>実行中かどうか。テスト・診断向けに公開する（他の公開状態プロパティと同じ理由）。</summary>
    public bool IsTutorialActive => _tutorialActive;

    /// <summary>
    /// 現在のステップ番号（1始まり、1〜<see cref="TutorialStepCount"/>）。テスト向けに公開する。
    /// 複数のステップが同じ主ボタン文言（「次へ」）を持つため、Graft.UiTests側がボタンの表示
    /// 文言だけでは「本当に次のステップまで進んだか」を判定できない（表示が変わらないまま
    /// 見かけ上一致してしまう）。この整数を実際のステップ確定の目印として使う。
    /// </summary>
    public int TutorialStepNumber => _tutorialStep + 1;

    /// <summary>
    /// 画面上のチュートリアルを開始する。初回起動ガイドの最終画面「使い方を学ぶ」
    /// （StartupCoordinator.StartAsync）・ツールバー「?」メニュー「使い方を学ぶ」・
    /// コマンドパレット（いずれも<see cref="ShellViewModel.RequestStartTutorial"/>経由）から呼ぶ。
    /// 実行中に呼んでも二重に始まらない。
    /// </summary>
    public void StartTutorial() => _ = StartTutorialAsync();

    private async Task StartTutorialAsync()
    {
        if (_tutorialActive) return;

        await SafeHandler.RunAsync("チュートリアルの開始", async () =>
        {
            _tutorialActive = true;
            // 不具合対応: ここでは0（ステップ1に相当）ではなく番兵の-1にする。0を入れると
            // TutorialStepNumberが「1」を返すようになり、実際にはまだ準備（サンプル生成等）が
            // 終わっておらず画面も未更新の段階で、外部（Graft.UiTests）から見て「もうステップ1に
            // 到達した」ように誤観測されてしまう（ShowTutorialStepAsyncのコメント参照）。
            // 実際の値は下のShowTutorialStepAsync(0)が、準備完了後・表示更新の直前に確定させる。
            _tutorialStep = -1;
            _tutorialSampleReady = false;
            _tutorialPatchAnalyzed = false;
            _tutorialSampleRoot = null;
            _tutorialPatchFilePath = null;
            _tutorialSampleProjectId = null;
            _tutorialPreviousProjectId = ViewModel.Graft.ProjectPane.SelectedItem?.Project.Id;

            // 再実行時に多重購読しないよう、開始のたびに一度外してから付け直す。
            TutorialOverlayControl.NextRequested -= OnTutorialNext;
            TutorialOverlayControl.BackRequested -= OnTutorialBack;
            TutorialOverlayControl.ExitRequested -= OnTutorialExit;
            TutorialOverlayControl.NextRequested += OnTutorialNext;
            TutorialOverlayControl.BackRequested += OnTutorialBack;
            TutorialOverlayControl.ExitRequested += OnTutorialExit;

            await ShowTutorialStepAsync(0).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async void OnTutorialNext(object? sender, EventArgs e) => await SafeHandler.RunAsync("チュートリアルの操作", async () =>
    {
        if (!_tutorialActive) return;

        // ステップ4「適用」・ステップ6「復元を体験」は、吹き出しの主ボタンを押した瞬間に
        // 実際の操作を行う（実ボタンと同じICommandを実行する。クラスコメント参照）。
        if (_tutorialStep == 3)
        {
            await ExecuteAndWaitAsync(ViewModel.Graft.ApplyCommand).ConfigureAwait(true);
        }
        else if (_tutorialStep == 5)
        {
            await ExecuteAndWaitAsync(ViewModel.Graft.History.RestoreCommand).ConfigureAwait(true);
        }

        // 実行を待っている間にEsc/「終了」で中断された場合は、ここで止める
        // （FinishTutorialAsyncが既に後片付け・オーバーレイの非表示まで済ませている）。
        if (!_tutorialActive) return;

        if (_tutorialStep >= TutorialStepCount - 1)
        {
            await FinishTutorialAsync().ConfigureAwait(true);
            return;
        }

        await ShowTutorialStepAsync(_tutorialStep + 1).ConfigureAwait(true);
    }).ConfigureAwait(true);

    private async void OnTutorialBack(object? sender, EventArgs e) => await SafeHandler.RunAsync("チュートリアルの操作", async () =>
    {
        if (!_tutorialActive || _tutorialStep <= 0) return;

        await ShowTutorialStepAsync(_tutorialStep - 1).ConfigureAwait(true);
    }).ConfigureAwait(true);

    private async void OnTutorialExit(object? sender, EventArgs e)
        => await SafeHandler.RunAsync("チュートリアルの終了", FinishTutorialAsync).ConfigureAwait(true);

    /// <summary>Escキー（ShellWindow.Keyboard.cs）から呼ぶ中断経路。</summary>
    private async Task ExitTutorialFromKeyboardAsync()
        => await SafeHandler.RunAsync("チュートリアルの終了", FinishTutorialAsync).ConfigureAwait(true);

    /// <summary>
    /// 指定したステップの準備（サンプル生成・解析・サイドビュー切替）を行い、オーバーレイへ反映する。
    ///
    /// 不具合対応: <see cref="_tutorialStep"/>（外部公開の<see cref="TutorialStepNumber"/>の元）は、
    /// 準備（サンプル生成・解析等、実I/Oを伴いawaitでこの関数を中断しうる）がすべて完了し、
    /// 実際にオーバーレイの表示（<see cref="TutorialOverlay.ShowStep"/>、対象のハイライト・
    /// 吹き出しの文言）を更新する直前でまとめて確定させる。呼び出し側（OnTutorialNext等）で
    /// 先に<c>_tutorialStep++</c>としてから呼ぶ実装だと、「ステップ番号だけが先に進み、
    /// 画面（対象のハイライト・吹き出しの文言）はまだ前のステップのまま」という一瞬の
    /// 不整合な状態が生じ、外部（Graft.UiTests）がTutorialStepNumberの変化を検知しても
    /// 実際の画面はまだ追いついていない、という競合状態を招く。ステップ番号の確定から
    /// ShowStep呼び出しまでの間に一切awaitを挟まない（＝同期的に連続して実行する）ことで、
    /// 外部から観測可能などの時点でも「ステップ番号」と「実際の表示」が必ず一致するようにする。
    /// </summary>
    private async Task ShowTutorialStepAsync(int step)
    {
        switch (step)
        {
            case 0:
                await PrepareSampleAsync().ConfigureAwait(true);
                break;
            case 1:
                await PrepareAnalyzeAsync().ConfigureAwait(true);
                break;
            case 4:
                ViewModel.SelectSideView(SideViewKind.History);
                break;
            case 5:
                ViewModel.SelectSideView(SideViewKind.History);
                ViewModel.Graft.History.SelectedItem = ViewModel.Graft.History.Items.FirstOrDefault();
                break;
        }

        if (!_tutorialActive) return; // 準備中に中断された場合。

        // サイドビューの開閉・接ぎ木パネルの展開等、直前の準備で起きたレイアウト変化が
        // 確定してから対象コントロールの位置を測る（ShellWindow.axaml.csの検索ビュー
        // フォーカスと同じ「1フレーム待つ」作法）。
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (!_tutorialActive) return;

        // ここから先はawaitを挟まない（クラスドキュメントコメント参照）。
        _tutorialStep = step;

        var target = ResolveTutorialTarget(_tutorialStep);
        var (title, message, primaryLabel) = GetTutorialStepText(_tutorialStep);
        TutorialOverlayControl.ShowStep(
            target,
            $"{_tutorialStep + 1} / {TutorialStepCount}",
            title,
            message,
            primaryLabel,
            backEnabled: _tutorialStep > 0);
    }

    /// <summary>
    /// ステップ1「サンプルプロジェクトを用意」。一時フォルダにサンプルを生成し、実際の
    /// フォルダ選択登録と同じ経路（<see cref="ProjectPaneViewModel.RegisterFolderAsync"/>）で
    /// 登録・選択する。戻る操作で再度このステップへ来ても、一度生成済みなら作り直さない。
    /// </summary>
    private async Task PrepareSampleAsync()
    {
        if (_tutorialSampleReady) return;

        var sample = OnboardingSample.Create();
        var patchFilePath = OnboardingSample.WritePatchFile(sample);

        var result = await ViewModel.Graft.ProjectPane.RegisterFolderAsync(sample.ProjectRoot).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            OnboardingSample.Cleanup(sample.ProjectRoot);
            OnboardingSample.CleanupPatchFile(patchFilePath);
            await FinishTutorialAsync().ConfigureAwait(true);
            return;
        }

        _tutorialSampleRoot = sample.ProjectRoot;
        _tutorialPatchFilePath = patchFilePath;
        _tutorialSampleProjectId = result.Value.Id;
        _tutorialSampleReady = true;
    }

    /// <summary>
    /// ステップ2「パッチを解析」。クリップボードは使わず、ファイルからの解析経路
    /// （4.1章、<see cref="MainViewModel.LoadPatchFromFileAsync"/>）でサンプルパッチを直接読み込む。
    /// 利用者のクリップボードの中身（実データ）に一切触れないための選択。
    /// </summary>
    private async Task PrepareAnalyzeAsync()
    {
        if (_tutorialPatchAnalyzed || _tutorialPatchFilePath is null) return;

        await ViewModel.Graft.LoadPatchFromFileAsync(_tutorialPatchFilePath).ConfigureAwait(true);
        _tutorialPatchAnalyzed = true;
    }

    /// <summary>ステップ番号から、指すべき対象コントロールのこのウィンドウ内での矩形を求める。</summary>
    private Rect? ResolveTutorialTarget(int step)
    {
        Control? control = step switch
        {
            0 => ProjectComboBox,
            1 => GraftPanelControl.ListBoxElement,
            // diffはエディタ領域の専用タブ（GraftPanel.axamlのコメント参照）として開くため、
            // EditorPane内から実際に表示中のDiffViewを探す。見つからない場合はエディタ領域全体を指す。
            2 => (Control?)FindVisibleDescendant<DiffView>(EditorHost) ?? EditorHost,
            // 接ぎ木パネルの「適用」ボタン。GraftPanel.axaml.csのDiffHostは実体がApplyButtonElement
            // （F6ペイン巡回の最後の停留先として同じボタンを指す既存の公開プロパティを再利用する）。
            3 => GraftPanelControl.DiffHost,
            4 => HistoryPaneControl.ListBoxElement,
            5 => HistoryPaneControl.RestoreButtonElement,
            6 => ShortcutsButton,
            _ => null,
        };

        if (control is null || !control.IsVisible || !control.IsAttachedToVisualTree()) return null;

        var topLeft = control.TranslatePoint(new Point(0, 0), TutorialOverlayControl);
        return topLeft is null ? null : new Rect(topLeft.Value, control.Bounds.Size);
    }

    /// <summary>対象のVisualツリーから、指定した型で現在表示中（IsVisible）の最初の子孫を探す。</summary>
    private static T? FindVisibleDescendant<T>(Visual root) where T : Visual
    {
        foreach (var descendant in root.GetVisualDescendants())
        {
            if (descendant is T typed && descendant is Control { IsVisible: true }) return typed;
        }
        return null;
    }

    private static (string Title, string Message, string PrimaryLabel) GetTutorialStepText(int step) => step switch
    {
        0 => ("サンプルプロジェクトを用意しました",
            "練習用のサンプルプロジェクトを一時フォルダに作成し、選択しました。これから接ぎ木——AIが提案した変更を安全にファイルへ適用する一連の流れ——を、実際の画面上で体験します。ここでの操作は一時フォルダ内のサンプルにのみ行われ、あなたの他のプロジェクトには一切影響しません。",
            "次へ"),
        1 => ("AIの出力を解析",
            "AIの出力を解析すると、ここ（接ぎ木パネル）に変更の一覧が出ます。今回はサンプルのパッチを実際に解析しました。",
            "次へ"),
        2 => ("差分を確認",
            "適用前に、何がどう変わるかを必ず確認できます。ブロックを選ぶと、このように差分が表示されます。",
            "次へ"),
        3 => ("適用",
            "「適用」を押すと実際にファイルが書き換わります。書き込む前には自動でバックアップが作られるので、あとからいつでも元に戻せます。下のボタンで、実際に適用してみましょう。",
            "適用する"),
        4 => ("履歴を確認",
            "適用は必ず記録され、バックアップが取られます。ここ（履歴）で、これまでに適用した変更を確認できます。",
            "次へ"),
        5 => ("復元を体験",
            "「このリビジョンを取り消す」を押すと、たった今適用した変更を元に戻せます。いつでも元に戻せるので、安心して試せます。下のボタンで、実際に1回戻してみましょう。",
            "元に戻す"),
        6 => ("以上が基本の流れです",
            "サンプルを使って、解析→差分確認→適用→履歴確認→復元の一連の流れを体験しました。詳しくはF1の取扱説明書をご覧ください。",
            "終了"),
        _ => (string.Empty, string.Empty, "次へ"),
    };

    /// <summary>
    /// AsyncRelayCommand.Executeはvoidを返す（async void）ため、外から完了を待てない。
    /// <c>IsExecuting</c>公開プロパティを見て完了を待つ（Graft.UiTests/ScenarioTests.csの
    /// ExecuteAsyncヘルパーと同じ作法）。同期コマンド（RelayCommand）ならExecute自体が
    /// 同期的に完了しているため、追加の待機は不要。
    /// </summary>
    private static async Task ExecuteAndWaitAsync(ICommand command)
    {
        command.Execute(null);

        if (command is AsyncRelayCommand async)
        {
            while (async.IsExecuting)
            {
                await Task.Delay(10).ConfigureAwait(true);
            }
        }
    }

    /// <summary>
    /// チュートリアルの終了（正常完了・Esc・「終了」ボタンのいずれからも呼ぶ共通の後片付け）。
    ///
    /// 【サンプルプロジェクトの登録を終了時に「登録解除する」判断】
    /// チュートリアルが生成したサンプルプロジェクトは、終了時に必ずプロジェクト一覧・一時
    /// フォルダの両方から取り除く（登録解除する）。理由:
    /// (1) サンプルは学習目的の使い捨てであり、実プロジェクトと区別なく一覧に残り続けると、
    ///     利用者が誤って選んで作業してしまう・一覧が煩雑になるリスクがある。
    /// (2) 「実データを絶対に触らない」という要件を、終了後の状態にも一貫して適用したい
    ///     （一時フォルダそのものは消さなくてもOSが最終的に片付けるが、Graft側の登録情報
    ///     projects.jsonにサンプルの痕跡を残さないほうが「触っていない」と言い切れる）。
    /// (3) 途中でEsc/「終了」により中断された場合と、最後まで完走した場合とで後片付けの
    ///     経路を分けない（常にこの1本にすることで、中断してもGraftの状態が壊れない
    ///     という要件を単純な実装で満たせる）。
    /// なお削除に失敗しても（他プロセスがファイルを掴んでいる等）チュートリアル自体は
    /// 必ず終了させる（SafeHandler経由の呼び出し元で例外は日本語通知される）。
    /// </summary>
    private async Task FinishTutorialAsync()
    {
        if (!_tutorialActive) return;

        // 不具合対応: IsTutorialActiveをここで即falseにすると、呼び出し元
        // （テスト・再実行ガード）が「後片付け（サンプルの登録解除・選択の復元）が
        // まだ完了していない」中途半端な状態を「もう終わった」と誤認してしまう
        // （実機・テストの双方で発生しうる競合状態）。後片付けが全て終わるまでは
        // trueのままにし、最後に倒す。二重実行はこの後片付けの間だけ、この関数の
        // 冒頭のガード（このifより上）で自然に弾かれる。

        TutorialOverlayControl.HideOverlay();
        TutorialOverlayControl.NextRequested -= OnTutorialNext;
        TutorialOverlayControl.BackRequested -= OnTutorialBack;
        TutorialOverlayControl.ExitRequested -= OnTutorialExit;

        if (_tutorialSampleProjectId is { } sampleId)
        {
            await ViewModel.Graft.ProjectPane.RemoveWithoutConfirmationAsync(sampleId, deleteHistory: true).ConfigureAwait(true);
        }
        if (_tutorialSampleRoot is { } root)
        {
            OnboardingSample.Cleanup(root);
        }
        if (_tutorialPatchFilePath is { } patchFile)
        {
            OnboardingSample.CleanupPatchFile(patchFile);
        }

        // チュートリアル前に選択していたプロジェクトへ戻す（実データを触らない要件の一部。
        // サンプルの登録解除自体もProjectPane.LoadAsyncを経由するため、既に何らかの
        // プロジェクトへ自動的に再選択されている可能性があるが、それが「チュートリアル前に
        // 選んでいたもの」と一致するとは限らないため、ここで明示的に選び直す）。
        if (_tutorialPreviousProjectId is { } previousId)
        {
            var previous = ViewModel.Graft.ProjectPane.Items.FirstOrDefault(i => i.Project.Id == previousId);
            if (previous is not null) ViewModel.Graft.ProjectPane.SelectedItem = previous;
        }

        _tutorialSampleRoot = null;
        _tutorialPatchFilePath = null;
        _tutorialSampleProjectId = null;
        _tutorialPreviousProjectId = null;
        _tutorialStep = 0;
        _tutorialActive = false; // 後片付けが全て終わってから最後に倒す（このメソッド冒頭のコメント参照）。
    }
}
