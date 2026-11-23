using System.Collections.ObjectModel;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Infra;
using Graft.Platform;

namespace Graft.ViewModels;

/// <summary>衝突ダイアログの一覧の1行（衝突したモジュール1件）。</summary>
public sealed class VbaConflictRowViewModel : ObservableObject
{
    private VbaConflictChoice _choice;

    public VbaConflictRowViewModel(VbaConflictItem item) => Item = item;

    public VbaConflictItem Item { get; }

    /// <summary>ファイル名（<c>Module1.bas</c>）。</summary>
    public string FileName => System.IO.Path.GetFileName(Item.RelativePath);

    /// <summary>フォルダ（<c>Modules</c>）。</summary>
    public string Directory => System.IO.Path.GetDirectoryName(Item.RelativePath)?.Replace('\\', '/') ?? string.Empty;

    public VbaConflictChoice Choice
    {
        get => _choice;
        set => SetProperty(ref _choice, value, () =>
        {
            OnPropertyChanged(nameof(ChoiceText));
            OnPropertyChanged(nameof(IsDecided));
        });
    }

    public bool IsDecided => _choice != VbaConflictChoice.Undecided;

    /// <summary>選んだ内容の表示（一覧の2行目）。</summary>
    public string ChoiceText => _choice switch
    {
        VbaConflictChoice.OverwriteExcel => "選択: Graft 側で上書き",
        VbaConflictChoice.AdoptExcel => "選択: Excel 側を採用",
        VbaConflictChoice.Skip => "選択: 反映しない",
        _ => "未選択",
    };

    public string AutomationName => $"{Item.RelativePath}、{ChoiceText}";
}

/// <summary>
/// 衝突ダイアログ（<see cref="Views.VbaConflictWindow"/>、仕様書_VBA連携 7.5）の ViewModel。
/// 衝突したモジュールごとに、Excel 側とミラー（Graft 側）の差分を既存の <see cref="DiffViewModel"/> で見せ、
/// 「Graft 側で上書き」「Excel 側を採用」「反映しない」「中止」「この適用を取り消す」から選ばせる。
/// 出す項目は場面（<see cref="VbaConflictRequest.Scene"/>）で決まる。
/// <para>
/// 【ボタンが決めるもの】 選択ボタンは、いま一覧で選んでいる 1 件に対する選択になり、次の未選択へ進む。
/// 「すべてに適用」にチェックがあれば全件へ同じ選択を付けて閉じる。全件が決まったら閉じる。
/// 窓を選ばずに閉じたとき（Esc・×）は <see cref="VbaConflictRequest.DefaultDecision"/>。
/// </para>
/// </summary>
public sealed class VbaConflictViewModel : ObservableObject
{
    private readonly VbaConflictRequest _request;
    private readonly int _contextLines;
    private readonly Dictionary<string, BlockPlan> _plans = new(StringComparer.OrdinalIgnoreCase);
    private VbaConflictRowViewModel? _selected;
    private bool _applyToAll;

    public VbaConflictViewModel(VbaConflictRequest request, Settings settings, IUiServices ui)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(ui);

        _contextLines = settings.Diff.ContextLines;
        Items = new ObservableCollection<VbaConflictRowViewModel>(request.Items.Select(i => new VbaConflictRowViewModel(i)));
        Diff = new DiffViewModel(settings, ui)
        {
            WordWrap = settings.Diff.WordWrap,
            ShowWhitespace = settings.Diff.ShowWhitespace,
        };

        OverwriteCommand = new RelayCommand(() => Choose(VbaConflictChoice.OverwriteExcel));
        AdoptCommand = new RelayCommand(() => Choose(VbaConflictChoice.AdoptExcel), () => CanAdoptExcel);
        SkipCommand = new RelayCommand(() => Choose(VbaConflictChoice.Skip), () => CanSkip);
        AbortCommand = new RelayCommand(() => Finish(VbaConflictDecision.Abort), () => CanAbort);
        UndoApplyCommand = new RelayCommand(
            () => Finish(new VbaConflictDecision(false, true, DecidedChoices(VbaConflictChoice.Skip))), () => CanUndoApply);

        SelectedItem = Items.FirstOrDefault();
    }

    /// <summary>衝突したモジュール。</summary>
    public ObservableCollection<VbaConflictRowViewModel> Items { get; }

    /// <summary>選んでいるモジュールの差分（左: Excel 側 / 右: Graft 側）。</summary>
    public DiffViewModel Diff { get; }

    public bool CanAdoptExcel => _request.CanAdoptExcel;

    public bool CanSkip => _request.CanSkip;

    public bool CanAbort => _request.CanAbort;

    public bool CanUndoApply => _request.CanUndoApply;

    /// <summary>見出し。場面によって、何が起きたかを一言で伝える。</summary>
    public string HeadingText => _request.Scene switch
    {
        VbaConflictScene.BeforeAnalysis => "解析する前に、Excel 側とファイル側の両方が変更されているモジュールがあります。",
        VbaConflictScene.Reanalysis => "解析したあとに、Excel 側とファイル側の両方が変更されているモジュールがあります。",
        VbaConflictScene.ManualPull => "Excel 側とファイル側の両方が変更されているため、取り込めないモジュールがあります。",
        VbaConflictScene.ContextCollect => "AI に渡すファイルを集める前に、Excel 側とファイル側の両方が変更されているモジュールがあります。中止すると、Graft 側（ミラー）の現在の内容で集めます。",
        VbaConflictScene.ManualPush => "Excel 側とファイル側の両方が変更されているため、反映できないモジュールがあります。",
        VbaConflictScene.AfterApply => "適用はファイルに済んでいますが、Excel 側も変更されているため、そのまま反映できないモジュールがあります。",
        _ => "取り消し・復元はファイルに済んでいますが、Excel 側も変更されているため、そのまま反映できないモジュールがあります。",
    };

    /// <summary>見出しの補足（選択肢の意味と、控えの場所）。</summary>
    public string GuideText => (_request.CanAdoptExcel
        ? "「Graft 側で上書き」は、Excel 側の本文を控えに残してから Graft の内容で置き換えます。「Excel 側を採用」は、Graft 側のファイルを Excel の内容で置き換えます。"
        : "「Graft 側で上書き」は、Excel 側の本文を控えに残してから Graft の内容で置き換えます。「反映しない」は、ファイルの変更を残し、Excel には未反映の印を付けます。")
        + ReasonGuide + RibbonGuide;

    /// <summary>
    /// 両側の変更ではない理由（Excel 側が空・要確認）で衝突しているモジュールがあるときの補足。理由を伏せたまま
    /// 「両方が変更されている」と読ませると、空の Excel を採用してミラーを空にする判断を、気付かずにさせてしまう。
    /// </summary>
    private string ReasonGuide
    {
        get
        {
            var parts = new List<string>();
            if (_request.Items.Any(i => i.Reason == VbaConflictReason.ExcelEmpty))
            {
                parts.Add("Excel 側が空（0 行）のモジュールがあります。「Excel 側を採用」を選ぶと、Graft 側のファイルも空になります。");
            }

            if (_request.Items.Any(i => i.Reason == VbaConflictReason.NeedsReview))
            {
                parts.Add("前回の書き込みで Excel 側を元に戻せなかった「要確認」のモジュールがあります。VBE で内容を確かめてから選んでください。");
            }

            return parts.Count == 0 ? string.Empty : " " + string.Join(" ", parts);
        }
    }

    /// <summary>
    /// リボン（customUI）の衝突があるときの補足。リボンの相手は Excel の中の本文ではなく、ブックのファイルの部品なので、
    /// 選択肢の動きがモジュールと違う（上書きは、ブックを閉じて開き直して書き換える）ことを伝える（仕様書_ブックの情報と依頼の観点 16.4）。
    /// </summary>
    private string RibbonGuide => _request.HasRibbon
        ? " リボン（CustomUI/）の「Excel 側」は、ブックのファイルに入っているリボンです。「Graft 側で上書き」を選ぶと、確認のあとにブックを" +
          "保存して閉じ、リボンを書き換えて開き直します（書き換える前のブック全体を控えに残します）。「Excel 側を採用」は、ブックのリボンでミラーのファイルを置き換えます。"
        : string.Empty;

    /// <summary>差分の見方。リボンだけの衝突では、左を「ブックのファイル側」と呼ぶ。</summary>
    public string DiffCaptionText => _request.Items.All(i => i.IsRibbon)
        ? "左: ブックのファイル側（リボン） / 右: Graft 側（ミラー）"
        : _request.HasRibbon ? "左: Excel 側（リボンはブックのファイル側） / 右: Graft 側（ミラー）" : "左: Excel 側 / 右: Graft 側（ミラー）";

    /// <summary>選択の進み具合（例: 「1 / 3 件を選択済み」）。</summary>
    public string ProgressText => $"{Items.Count(i => i.IsDecided)} / {Items.Count} 件を選択済み";

    public bool HasMultiple => Items.Count > 1;

    /// <summary>「すべてに適用」。チェック中は、選択ボタンが全件に効いて閉じる。</summary>
    public bool ApplyToAll { get => _applyToAll; set => SetProperty(ref _applyToAll, value); }

    public VbaConflictRowViewModel? SelectedItem
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            if (value is null)
            {
                Diff.Clear();
                return;
            }

            if (!_plans.TryGetValue(value.Item.RelativePath, out var plan))
            {
                plan = VbaConflictDiff.BuildPlan(value.Item, _contextLines);
                _plans[value.Item.RelativePath] = plan;
            }

            Diff.Load(plan);
        }
    }

    public RelayCommand OverwriteCommand { get; }

    public RelayCommand AdoptCommand { get; }

    public RelayCommand SkipCommand { get; }

    public RelayCommand AbortCommand { get; }

    public RelayCommand UndoApplyCommand { get; }

    /// <summary>決まった結果。窓が閉じるまでは null。</summary>
    public VbaConflictDecision? Result { get; private set; }

    /// <summary>結果が決まって窓を閉じてよいことの通知。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>窓を選ばずに閉じたときの結果にする。</summary>
    public void CloseWithoutChoosing()
    {
        Result ??= _request.DefaultDecision;
    }

    private void Choose(VbaConflictChoice choice)
    {
        if (ApplyToAll)
        {
            Finish(VbaConflictDecision.All(_request, choice));
            return;
        }

        if (SelectedItem is null) return;
        SelectedItem.Choice = choice;
        OnPropertyChanged(nameof(ProgressText));

        var next = Items.FirstOrDefault(i => !i.IsDecided);
        if (next is null)
        {
            Finish(new VbaConflictDecision(false, false, DecidedChoices(choice)));
            return;
        }

        SelectedItem = next;
    }

    /// <summary>選び済みの内容を返す。未選択は <paramref name="fallback"/> として扱う（「この適用を取り消す」の途中など）。</summary>
    private Dictionary<string, VbaConflictChoice> DecidedChoices(VbaConflictChoice fallback)
        => Items.ToDictionary(i => i.Item.RelativePath, i => i.IsDecided ? i.Choice : fallback, StringComparer.OrdinalIgnoreCase);

    private void Finish(VbaConflictDecision decision)
    {
        Result = decision;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
