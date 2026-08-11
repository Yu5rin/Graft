using System.Collections.ObjectModel;
using Graft.Core;
using Graft.Platform;
using Graft.ViewModels;

namespace Graft.Editor;

/// <summary>
/// 製品としての使い勝手3件のうち機能3（Ctrl+Shift+Tで直前に閉じたタブを開き直す）で
/// 1件分の記録として保持する情報。ファイルの絶対パスと、閉じた時点のカーソル位置。
/// </summary>
public readonly record struct ClosedTabRecord(string FullPath, int CaretLine, int CaretColumn);

/// <summary>
/// エディタタブの生成・復元・保存確認を担当する（19章）。<see cref="Graft.ViewModels.EditorPaneViewModel"/>
/// から呼び出される、ObservableObjectではない純粋なロジック層。プレビュータブの置換規則
/// （4.3節）とCtrl+Tab用の直近使用順（MRU）もここで管理する。
/// 保存/破棄/キャンセルの3択確認は<see cref="IDialogService.ConfirmThreeWayAsync"/>を使う。
///
/// 機能3（Ctrl+Shift+Tで直前に閉じたタブを開き直す）: ユーザーが明示的に閉じたタブ
/// （<see cref="CloseAsync"/>経由）のパスとカーソル位置を、新しい順に最大
/// <see cref="MaxClosedTabRecords"/>件だけ<see cref="_closedTabs"/>に保持する。プレビュータブの
/// 自動置換（<see cref="OpenAsync"/>内、新しいファイルを開いたことで押し出されただけ）や、
/// エクスプローラでのファイル削除に追従した強制クローズ（<see cref="NotifyDeletedAsync"/>）は
/// ユーザーが「閉じた」と意識する操作ではないため記録しない。
/// </summary>
public sealed class EditorTabManager
{
    private readonly IDialogService _dialogs;
    private readonly List<EditorTabViewModel> _mru = new();
    private readonly List<ClosedTabRecord> _closedTabs = new();
    private const int MaxClosedTabRecords = 10;
    private string? _projectRoot;

    public EditorTabManager(IDialogService dialogs)
    {
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
    }

    public ObservableCollection<EditorTabViewModel> Tabs { get; } = new();

    /// <summary>切替先のプロジェクトルートを設定する。タブは呼び出し側が事前に閉じておくこと。</summary>
    public void SetProjectRoot(string? projectRoot)
    {
        _projectRoot = projectRoot;
        _mru.Clear();
        // 機能3: 閉じたタブの記録も、Ctrl+Tabの直近使用順（MRU）と同じくプロジェクト単位で
        // 意味を持つ情報のため、切替時にクリアする（別プロジェクトで閉じたファイルをCtrl+Shift+T
        // で開き直せてしまうと混乱するため）。
        _closedTabs.Clear();
    }

    /// <summary>ファイルを開く。既に開いている場合はそのタブを返す（プレビュー→固定への昇格のみ行う）。</summary>
    public async Task<GraftResult<EditorTabViewModel>> OpenAsync(string fullPath, bool preview, CancellationToken ct)
    {
        var existing = Tabs.FirstOrDefault(t => PathsEqual(t.Session.FullPath, fullPath));
        if (existing is not null)
        {
            if (!preview) existing.IsPreview = false;
            return GraftResult<EditorTabViewModel>.Ok(existing);
        }

        var opened = await DocumentSession.OpenAsync(fullPath, _projectRoot ?? string.Empty, ct).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return GraftResult<EditorTabViewModel>.Fail(opened.Issues);
        }

        if (preview)
        {
            // 4.3: 未編集のプレビュータブは確認なしで次のプレビューに置換される。
            var oldPreview = Tabs.FirstOrDefault(t => t.IsPreview);
            if (oldPreview is not null) RemoveTab(oldPreview);
        }

        var tab = new EditorTabViewModel(opened.Value, CloseAsync) { IsPreview = preview };
        Tabs.Add(tab);
        return GraftResult<EditorTabViewModel>.Ok(tab, opened.Issues);
    }

    /// <summary>未保存なら保存確認を挟んでタブを閉じる。falseはユーザーがキャンセルしたことを表す。</summary>
    public async Task<bool> CloseAsync(EditorTabViewModel tab)
    {
        if (tab.Session.IsModified)
        {
            // 不具合修正（実機報告）: ボタンの並びを「保存」「破棄」「キャンセル」（Windowsの
            // 作法と逆）から「保存」「保存しない」「キャンセル」へ直す指示だったが、
            // 「保存されていない変更があります。保存しますか？」という疑問文に対しては
            // 「はい」「いいえ」の方が問いと答えの形が揃う（「保存しますか？」→「保存」/
            // 「保存しない」だと問いと答えが重複してかえって分かりにくい）ため、
            // 最終的に「はい」「いいえ」を採用した。並び順は変わらず「肯定→否定→キャンセル」
            // （AvaloniaDialogService.ConfirmThreeWayAsync参照）で、「はい」＝保存が既定
            // ボタン（Enter）になる。保存は非破壊的な操作のため、既定ボタンにしてよい。
            var message = $"「{tab.Title}」には保存されていない変更があります。保存しますか？";
            var choice = await _dialogs.ConfirmThreeWayAsync("変更の保存", message, "はい", "いいえ").ConfigureAwait(true);
            if (choice is null) return false; // キャンセル
            if (choice == true)
            {
                var saved = await tab.Session.SaveAsync().ConfigureAwait(true);
                if (!saved.IsSuccess) return false; // 保存に失敗した場合は閉じず編集を残す
            }
        }

        RecordClosedTab(tab);
        RemoveTab(tab);
        return true;
    }

    /// <summary>全タブを閉じる。途中でキャンセルされた場合はそこで打ち切りfalseを返す。</summary>
    public async Task<bool> CloseAllAsync()
    {
        foreach (var tab in Tabs.ToList())
        {
            if (!await CloseAsync(tab).ConfigureAwait(true)) return false;
        }

        return true;
    }

    /// <summary>指定パスのうち未保存の変更があるタブの絶対パス一覧を返す（4.8）。</summary>
    public IReadOnlyList<string> FindUnsaved(IEnumerable<string> fullPaths)
    {
        var set = new HashSet<string>(fullPaths, PathComparer);
        return Tabs.Where(t => t.Session.IsModified && set.Contains(t.Session.FullPath))
            .Select(t => t.Session.FullPath).ToList();
    }

    /// <summary>指定パスに該当する開いているタブを、確認なしで再読込する（4.8: 適用後の再読込）。</summary>
    public async Task ReloadIfOpenAsync(IEnumerable<string> fullPaths)
    {
        var set = new HashSet<string>(fullPaths, PathComparer);
        foreach (var tab in Tabs.Where(t => set.Contains(t.Session.FullPath)).ToList())
        {
            await tab.Session.ReloadAsync().ConfigureAwait(true);
        }
    }

    /// <summary>エクスプローラでのリネーム・移動に追従し、該当タブのパス表示を更新する（4.2/4.8）。</summary>
    public void NotifyRenamed(string oldFullPath, string newFullPath)
    {
        var tab = Tabs.FirstOrDefault(t => PathsEqual(t.Session.FullPath, oldFullPath));
        if (tab is null) return;

        tab.Session.UpdatePath(newFullPath, _projectRoot ?? string.Empty);
        tab.NotifyPathChanged();
    }

    /// <summary>
    /// エクスプローラでの削除に追従し、該当タブを保存確認なしで閉じる（4.2/4.8）。
    /// 実体が既に無いファイルへの保存確認は意味を持たないため、<see cref="CloseAsync"/>とは
    /// 別に確認なしのタブ除去を行う。
    /// </summary>
    public Task NotifyDeletedAsync(string fullPath)
    {
        var tab = Tabs.FirstOrDefault(t => PathsEqual(t.Session.FullPath, fullPath));
        if (tab is not null) RemoveTab(tab);
        return Task.CompletedTask;
    }

    /// <summary>タブがアクティブ化されたことを記録し、MRU順の先頭へ移動する（Ctrl+Tab用）。</summary>
    public void Touch(EditorTabViewModel tab)
    {
        _mru.Remove(tab);
        _mru.Insert(0, tab);
    }

    /// <summary>
    /// 機能改善（タブのドラッグ並べ替え）: <paramref name="tab"/>を、移動前の並び順における
    /// <paramref name="targetIndex"/>の位置（0=先頭）へ挿入し直す。<see cref="Touch"/>が管理する
    /// MRU順（Ctrl+Tabの直近使用順）は表示順とは別物のため一切変更しない
    /// （タブの見た目の並びを変えても、Ctrl+Tabでの切替順は変わらないままにする）。
    ///
    /// <see cref="ObservableCollection{T}.Move"/>は「削除後のインデックス」を期待するため
    /// （内部でRemoveItemしてからInsertItemする実装のため、削除で1つ詰まった後の位置を
    /// 渡す必要がある）、呼び出し側にとって直感的な「移動前の並びでの挿入先」を受け取り、
    /// ここで吸収する。
    /// </summary>
    public void MoveTab(EditorTabViewModel tab, int targetIndex)
    {
        var oldIndex = Tabs.IndexOf(tab);
        if (oldIndex < 0) return;

        // targetIndexは移動前の並び（要素数Tabs.Count）における挿入先で、0=先頭、
        // Tabs.Count=末尾（最後の要素より後ろ）まで有効（Tabs.Count - 1ではない。
        // 末尾ドロップを「最後から2番目」に丸めてしまわないよう上限をCountにする）。
        var clampedTarget = Math.Clamp(targetIndex, 0, Tabs.Count);
        var newIndex = clampedTarget > oldIndex ? clampedTarget - 1 : clampedTarget;
        if (newIndex == oldIndex) return;

        Tabs.Move(oldIndex, newIndex);
    }

    /// <summary>
    /// Ctrl+Tabで切り替える先のタブ（直近2番目に使用したタブ）を返す。押すたびに直近2件を
    /// 往復する簡易実装。Ctrl長押しによる連続循環（モーダルなオーバーレイ表示）はE1の対象外。
    /// </summary>
    public EditorTabViewModel? NextByMru() => _mru.Count >= 2 ? _mru[1] : null;

    /// <summary>閉じた直後（<see cref="RemoveTab"/>で実体を破棄する前）のタブの情報を記録する。</summary>
    private void RecordClosedTab(EditorTabViewModel tab)
    {
        var record = new ClosedTabRecord(tab.Session.FullPath, tab.CaretLine, tab.CaretColumn);
        // 同じファイルの古い記録が残っていれば1件にまとめ、直近に閉じたときの位置を優先する
        // （同じファイルを開いては閉じてを繰り返しても履歴が同じパスで埋まらないようにする）。
        _closedTabs.RemoveAll(r => PathsEqual(r.FullPath, record.FullPath));
        _closedTabs.Insert(0, record);
        if (_closedTabs.Count > MaxClosedTabRecords) _closedTabs.RemoveAt(_closedTabs.Count - 1);
    }

    /// <summary>機能3: 「元に戻せる（開き直せる）」閉じたタブの記録が1件でもあるかどうか。</summary>
    public bool HasClosedTabs => _closedTabs.Count > 0;

    /// <summary>
    /// 機能3（Ctrl+Shift+T）。最も新しく閉じたタブの記録を1件取り出す。実体がもう存在しない
    /// ファイル（削除・移動された等）の記録は自動的に読み飛ばし、開き直せる最初の1件を返す。
    /// 取り出した記録（読み飛ばした分も含む）はスタックから取り除かれる。
    /// </summary>
    /// <param name="skippedMissing">1件以上、実体が既に無いために読み飛ばした記録があった場合true。
    /// 戻り値がnullで、かつこれがtrueのとき、呼び出し側は「記録はあったが復元できなかった」と
    /// 判別できる（記録が最初から0件だった場合と区別するため）。</param>
    public ClosedTabRecord? TakeNextReopenable(out bool skippedMissing)
    {
        skippedMissing = false;
        while (_closedTabs.Count > 0)
        {
            var candidate = _closedTabs[0];
            _closedTabs.RemoveAt(0);
            if (File.Exists(candidate.FullPath)) return candidate;
            skippedMissing = true;
        }
        return null;
    }

    private void RemoveTab(EditorTabViewModel tab)
    {
        Tabs.Remove(tab);
        _mru.Remove(tab);
        tab.DetachEvents();
        tab.Session.Dispose();
    }

    // Windowsではファイルパスの大文字小文字を区別しない。テスト実行環境（Linux）では
    // Editor/ がテストプロジェクトに取り込まれないため、実行時のOSに合わせるだけでよい。
    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static bool PathsEqual(string a, string b) => PathComparer.Equals(a, b);
}
