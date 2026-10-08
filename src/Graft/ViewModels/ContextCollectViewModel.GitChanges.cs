using Graft.Features;

namespace Graft.ViewModels;

/// <summary>「gitの変更ファイルだけ」ボタンが使えるかどうか。</summary>
public enum GitAvailability
{
    /// <summary>窓を開いた直後で、まだ git を確認していない。</summary>
    Checking,

    /// <summary>git が使え、プロジェクトがリポジトリの中にある。</summary>
    Available,

    /// <summary>git コマンドが見つからない。</summary>
    GitNotFound,

    /// <summary>git は使えるが、プロジェクトがリポジトリの中にない。</summary>
    NotARepository,
}

/// <summary>
/// コンテキスト収集ViewModelのうち、「gitの変更ファイルだけ」を選ぶ操作を担う部分。
/// 本体が大きいため分割ファイルにしている。git の出力の解釈は
/// <see cref="GitChangedFilesParser"/>、選択状態の組み立ては<see cref="ChangedFileSelection"/>
/// （どちらも純ロジック）に任せ、ここは確認・反映・保存・ステータス表示を受け持つ。
/// </summary>
public sealed partial class ContextCollectViewModel
{
    private GitAvailability _gitAvailability = GitAvailability.Checking;

    /// <summary>「gitの変更ファイルだけ」ボタン。</summary>
    public AsyncRelayCommand SelectGitChangedCommand { get; }

    /// <summary>ボタンを押せるか（git が使え、走査中でない）。ツールチップの出し分けにも使う。</summary>
    public GitAvailability GitAvailabilityState => _gitAvailability;

    /// <summary>
    /// ボタンのツールチップ（標準の説明）。押せないときは**理由**を書く
    /// （ボタンが灰色なだけでは、なぜ使えないのか利用者に分からないため）。
    /// </summary>
    public string GitSelectToolTip => _gitAvailability switch
    {
        GitAvailability.Available =>
            "未コミットの変更があるファイルだけを「内容も出す」にし、ほかのファイルはすべて「構成だけ」にします。押すと、置き換える前に確認します。",
        GitAvailability.GitNotFound =>
            "gitが見つからないため使えません。gitをインストールし、コマンドとして実行できる状態（PATH）にしてから窓を開き直してください。",
        GitAvailability.NotARepository =>
            "このプロジェクトのフォルダはgitのリポジトリの中にないため使えません。",
        _ => "gitの状態を確認しています…",
    };

    /// <summary>ボタンのツールチップ（くわしい説明）。押せないときは<see cref="GitSelectToolTip"/>と同じ理由。</summary>
    public string GitSelectDetailedToolTip => _gitAvailability == GitAvailability.Available
        ? "作業中のファイル（変更したもの・新しく作ったもの・名前を変えたもの）だけをAIへ渡したいときに使います。"
          + "変更のあるファイルを「内容も出す」に、それ以外のすべてのファイルを「構成だけ」にします。"
          + "全体の形はAIに伝わり、中身は変更したファイルだけになります。削除したファイルは対象外です。"
          + "いまの選択をまとめて書き換えるため、押すと先に確認が出ます。変更が0件のときや、"
          + "変更ファイルがすべて除外されていて選べないときは、選択を変えずにその旨を表示します。"
        : GitSelectToolTip;

    /// <summary>
    /// git の確認結果を反映する。窓を開いたときの確認（<see cref="InitializeAsync"/>）と、
    /// 押したあとの取得で git が使えないと分かったときに呼ぶ。
    /// </summary>
    private void ApplyGitAvailability(GitCommitPreflight preflight)
        => SetGitAvailability(preflight switch
        {
            GitCommitPreflight.Ready => GitAvailability.Available,
            GitCommitPreflight.GitCommandNotFound => GitAvailability.GitNotFound,
            _ => GitAvailability.NotARepository,
        });

    private void SetGitAvailability(GitAvailability availability)
    {
        if (_gitAvailability == availability) return;
        _gitAvailability = availability;
        OnPropertyChanged(nameof(GitAvailabilityState));
        OnPropertyChanged(nameof(GitSelectToolTip));
        OnPropertyChanged(nameof(GitSelectDetailedToolTip));
        SelectGitChangedCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// git の未コミットの変更ファイルだけを「内容も出す」にし、それ以外のファイルをすべて
    /// 「構成だけ」にする。**既存の選択をまとめて書き換える**ため、置き換える前に必ず確認する。
    ///
    /// 【設計判断】
    /// <list type="bullet">
    /// <item>変更が0件、または変更ファイルがすべて除外されていて選べないときは、確認も出さず
    /// 選択も変えない（全ファイルを「構成だけ」にしても利用者の役に立たず、手で整えた選択を
    /// 失うだけのため）。理由はステータスに書く。</item>
    /// <item>除外されていて選べなかった変更ファイルは、黙って落とさずパスと理由をステータスに書く
    /// （「変更したのにAIに渡っていない」を見落とさないため。AIの「このファイルも見せて」
    /// <see cref="ApplyRequestedFilesAsync"/>と同じ方針）。</item>
    /// <item>状態の保存は手動操作と同じ<see cref="PersistFileStatesAsync"/>に乗せ、
    /// デバウンスは待たずその場で保存する（ウィンドウを開いてすぐ閉じても失われないように。
    /// <see cref="ApplyRequestedFilesAsync"/>と同じ）。</item>
    /// <item>収集モードが「ツリーのみ」「差分のみ」のときは、ファイルの中身が出力に載らず
    /// 「選んだのに出ない」になるため、「ツリー＋選択」に切り替えてその旨を書く
    /// （<see cref="ApplyRequestedFilesAsync"/>と同じ考え方）。</item>
    /// <item>絞り込み中でも、対象は絞り込みで見えているものに限らず**全ファイル**。
    /// 「gitの変更」という基準は一覧の見た目と無関係なため。一覧は同じノードを見せているので、
    /// 反映後の状態がそのまま表示に現れる。</item>
    /// </list>
    /// </summary>
    private async Task SelectGitChangedAsync()
    {
        if (_lastScan.Count == 0)
        {
            StatusMessage = "対象のファイルがありません。";
            return;
        }

        StatusMessage = "gitの変更ファイルを調べています…";
        var changes = await _git.GetChangedFilesAsync(_project.Root).ConfigureAwait(true);

        switch (changes.State)
        {
            case GitChangedFilesState.GitNotFound:
                SetGitAvailability(GitAvailability.GitNotFound);
                StatusMessage = "gitが見つからないため、変更ファイルを調べられませんでした。選択は変えていません。";
                return;
            case GitChangedFilesState.NotARepository:
                SetGitAvailability(GitAvailability.NotARepository);
                StatusMessage = "このプロジェクトのフォルダはgitのリポジトリの中にないため、変更ファイルを調べられませんでした。選択は変えていません。";
                return;
            case GitChangedFilesState.Failed:
                StatusMessage = $"gitの変更ファイルを取得できませんでした。選択は変えていません。{FirstLine(changes.Detail)}";
                return;
        }

        var selection = ChangedFileSelection.Build(_lastScan, changes.Paths);

        if (selection.FullPaths.Count == 0)
        {
            StatusMessage = BuildGitChangedNothingStatus(changes, selection);
            return;
        }

        var previousMode = _selectedMode;
        var modeWillChange = previousMode is ContextMode.TreeOnly or ContextMode.ChangedSince;
        var confirmed = await _dialogs.ConfirmAsync(
            "gitの変更ファイルだけを選ぶ",
            BuildGitChangedConfirmMessage(selection, modeWillChange, previousMode)).ConfigureAwait(true);
        if (!confirmed)
        {
            StatusMessage = "取り消しました。選択は変えていません。";
            return;
        }

        foreach (var node in Files)
        {
            if (node.IsDirectory || node.IsExcluded) continue;
            if (selection.States.TryGetValue(node.RelativePath, out var state)) node.State = state;
        }

        if (modeWillChange) SelectedMode = ContextMode.TreeAndSelected;

        RecomputeDirectoryStates();
        UpdateApproxTokenEstimate();
        _persistTimer.Stop();
        await PersistFileStatesAsync().ConfigureAwait(true);

        StatusMessage = BuildGitChangedStatus(changes, selection, modeWillChange, previousMode);
    }

    private string BuildGitChangedConfirmMessage(ChangedFileSelection selection, bool modeWillChange, ContextMode previousMode)
    {
        var lines = new List<string>
        {
            $"今の選択を、gitの変更ファイル{selection.FullPaths.Count}件だけ内容を出す状態に置き換えます。",
            $"それ以外のファイル（{selection.StructureOnlyCount}件）はすべて「構成だけ」になります。手で切り替えた今の状態は失われます。",
        };
        if (selection.Excluded.Count > 0)
        {
            lines.Add($"除外されていて選べない変更ファイルが{selection.Excluded.Count}件あります（内容は出ません）。");
        }
        if (modeWillChange)
        {
            lines.Add($"収集モードが「{ModeLabel(previousMode)}」だとファイルの内容が出力されないため、「ツリー＋選択」に切り替えます。");
        }
        lines.Add("よろしいですか？");
        return string.Join("\n\n", lines);
    }

    private string BuildGitChangedNothingStatus(GitChangedFiles changes, ChangedFileSelection selection)
    {
        var parts = new List<string>();
        if (selection.Excluded.Count > 0)
        {
            parts.Add($"gitの変更ファイルは{selection.Excluded.Count}件ありますが、すべて除外されていて選べません。");
            parts.Add(DescribeExcludedChanges(selection));
        }
        else
        {
            parts.Add("gitの変更ファイルはありません。");
        }

        if (changes.DeletedCount > 0) parts.Add($"削除されたファイル{changes.DeletedCount}件は対象外です。");
        parts.Add("選択は変えていません。");
        return string.Join(" ", parts);
    }

    private string BuildGitChangedStatus(
        GitChangedFiles changes, ChangedFileSelection selection, bool modeChanged, ContextMode previousMode)
    {
        var parts = new List<string>
        {
            $"gitの変更ファイル{selection.FullPaths.Count}件を「内容も出す」にし、ほかの{selection.StructureOnlyCount}件は「構成だけ」にしました。",
        };

        if (modeChanged)
        {
            parts.Add($"収集モードが「{ModeLabel(previousMode)}」だとファイルの内容が出力されないため、「ツリー＋選択」に切り替えました。");
        }

        if (selection.Excluded.Count > 0)
        {
            parts.Add($"除外されていて選べなかった変更ファイルが{selection.Excluded.Count}件あります。");
            parts.Add(DescribeExcludedChanges(selection));
        }

        if (changes.DeletedCount > 0) parts.Add($"削除されたファイル{changes.DeletedCount}件は対象外です。");
        if (selection.OtherCount > 0) parts.Add($"フォルダなどファイルとして扱えなかった変更が{selection.OtherCount}件あります。");

        return string.Join(" ", parts);
    }

    /// <summary>除外されて選べなかった変更ファイルを「パス（理由）」の形で並べる。</summary>
    private static string DescribeExcludedChanges(ChangedFileSelection selection)
    {
        var items = selection.Excluded.Select(e => string.IsNullOrEmpty(e.ExcludeReason)
            ? e.Requested
            : $"{e.Requested}（{e.ExcludeReason}）");
        return $"選べなかったもの: {JoinLimited(items)}。";
    }

    private string ModeLabel(ContextMode mode) => Modes.FirstOrDefault(m => m.Mode == mode)?.Label ?? mode.ToString();

    /// <summary>git の出力のうち最初の1行（長すぎる場合は切り詰め）。原因の手掛かりとしてステータスに添える。</summary>
    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var line = text.Trim().Split('\n')[0].TrimEnd('\r');
        return line.Length > 160 ? line[..160] + "…" : line;
    }
}
