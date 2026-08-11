using Graft.Core;
using Graft.Features;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> の分割ファイル（1ファイル400行上限のため）。
/// 仕様書3.3/3.4「プロジェクト自動判定」の配線を担う。<see cref="ProjectMatcher"/>自体は
/// 実装済みだったが、パッチ解析後のフローから一度も呼ばれておらず、選択中のプロジェクトと
/// 無関係なパッチでも無警告で適用できてしまっていた（クラスコメントに明記された
/// 「呼び出し側で無効化できてはならない必須機構」という設計意図に反する状態）。
///
/// 配線先は<see cref="RunDryRunAsync"/>の冒頭ひとつに固定する。ドライラン（プレビュー・適用の
/// 前段）へ到達する経路は ParseTextAndLoadAsync（クリップボード・ファイル・D&amp;D）、
/// MergeQueueAndLoadAsync（4.10 分割パッチの結合）、PreviewCommand の再実行など複数あるが、
/// いずれも最終的にRunDryRunAsyncを通るため、個々の入口ではなくここへ差し込むことで
/// 「新しい入口を追加したら判定を通し忘れる」事故を構造的に防ぐ（無効化不可の要件に対応する）。
/// </summary>
public sealed partial class MainViewModel
{
    private readonly ProjectMatcher _projectMatcher = new();

    /// <summary>
    /// 直近に自動判定を済ませた組み合わせ（同一パッチ参照＋同一プロジェクトID）。
    /// 「プレビュー」ボタンの再クリックのたびに確認ダイアログが再度出るのを防ぐためのキャッシュ。
    /// パッチが差し替わる、またはプロジェクトが切り替わればキャッシュは自然に無効化される
    /// （参照比較・ID比較のため、明示的なクリア処理は不要）。
    /// </summary>
    private Patch? _matchEvaluatedPatch;
    private string? _matchEvaluatedProjectId;

    /// <summary>
    /// 仕様書3.3/3.4 プロジェクト自動判定。<see cref="RunDryRunAsync"/>の冒頭から必ず呼ぶ。
    /// true: そのまま（切替が起きた場合はその後の）プロジェクトで続行してよい。
    /// false: 適用フローへ進めない（ブロック、またはユーザーが確認ダイアログをキャンセルした）。
    /// 呼び出し元は戻り値がfalseなら即座にRunDryRunAsyncを打ち切ること。
    /// </summary>
    private async Task<bool> EnsureProjectMatchAsync(Project project)
    {
        if (_currentPatch is null) return true;

        if (ReferenceEquals(_matchEvaluatedPatch, _currentPatch) && _matchEvaluatedProjectId == project.Id)
        {
            return true;
        }

        var connected = ProjectPane.Items.Select(i => i.Project).Where(p => !p.IsDisconnected).ToList();
        if (connected.Count <= 1)
        {
            // 比較対象が無い（登録された接続済みプロジェクトが0〜1件）。この場合は判定そのものに
            // 意味が無い（唯一の候補しかないのに「一致率が低い」と警告しても、利用者に取れる
            // 行動が無く、無意味な警告になるだけ）ため、ProjectMatcherは呼ばずに素通りさせる。
            MarkMatchEvaluated(project);
            return true;
        }

        var outcome = await _projectMatcher.MatchAsync(_currentPatch, connected).ConfigureAwait(true);
        if (!outcome.IsSuccess || outcome.Value.Best is null)
        {
            // パッチの全ブロックがFULL形式・MKDIRなど新規作成前提で、一致率の算出自体ができない
            // ケース（ProjectMatcher.BuildUndeterminableOutcome）。新規ファイルの追加はごく
            // 日常的な操作であり、そのたびに「判定できません」と確認を挟むと煩わしいだけで
            // 安全性への寄与も薄いため、ここでは黙って現在のプロジェクトで続行する
            // （ProjectMatcher自身が返すWarning issueはUI側で警告ダイアログとしては表示しない
            // 判断。無意味な警告を出さない、という実装依頼の方針を優先した）。
            MarkMatchEvaluated(project);
            return true;
        }

        var best = outcome.Value.Best;

        // Blockedタイの罠に注意: 一致率でOrderByDescendingした際、複数プロジェクトが同率
        // （典型的には全滅で0%）で並ぶと、安定ソートにより「たまたま一覧の並び順が早い」
        // プロジェクトがBestになる。選択中のプロジェクトがその「たまたま先頭に来ただけ」の
        // 候補である場合、以下の「best==currentなら無警告」判定を先に行ってしまうと、
        // 本来なら誰にも一致しない（＝ブロックされるべき）パッチが黙って通ってしまう
        // （実機のXvfb確認で実際に踏んだ不具合。projC選択中、存在しないパスばかりの
        // パッチが無警告でロードされてしまった）。そのためBlocked判定を必ず先に見る。
        if (outcome.Value.Decision == ProjectMatchDecision.Blocked)
        {
            return BlockPatch(project, best);
        }

        if (best.Project.Id == project.Id)
        {
            // 選択中のプロジェクトが最有力候補（かつBlocked相当の低さではない）。切り替える先が
            // 無く誤爆の恐れも無いため、一致率が90%未満（NeedsConfirmation相当）でも警告は出さない。
            // 「選択中のプロジェクトと一致するパッチでは何の警告も出ない（誤検知しない）」ことを
            // 優先する設計判断。
            MarkMatchEvaluated(project);
            return true;
        }

        // ここから先は最有力候補が「今開いているプロジェクトとは別」（かつBlockedではない）場合。
        // 仕様書v2.0 3.3「自動判定の結果いま開いているプロジェクトと別のプロジェクトが選ばれた
        // 場合は、切替の確認を必ず挟む」に従い、一致率が90%以上（AutoSelected）であっても
        // 無条件に切り替えはしない（黙って切り替わると利用者が混乱するため。3.4のenumドキュメント
        // 上「AutoSelected=自動選択」だが、実際に別プロジェクトへ切り替える瞬間だけは常に
        // 確認を挟む——この点は3.3の記述を優先し、コード上の判断理由として明記する）。
        return await ConfirmProjectSwitchAsync(project, outcome.Value).ConfigureAwait(true);
    }

    private void MarkMatchEvaluated(Project project)
    {
        _matchEvaluatedPatch = _currentPatch;
        _matchEvaluatedProjectId = project.Id;
    }

    /// <summary>
    /// 一致率50%未満（最有力候補ですら低い）。誤爆の可能性が高いため適用フローへ進めない。
    /// なぜブロックされたか・どうすればよいかが伝わるよう、最有力候補名と一致率、
    /// 取るべき行動（手動でのプロジェクト選択・パッチ内容の確認）を具体的に文章化する。
    /// 最有力候補が選択中のプロジェクト自身であるケース（登録済みのどのプロジェクトにも
    /// 一致しない＝Blockedタイで選択中がたまたま先頭に来た場合）は、「他プロジェクトへの
    /// 切替」を勧めても意味が無いため文言を分ける。
    /// </summary>
    private bool BlockPatch(Project project, ProjectMatchCandidate best)
    {
        DiscardCurrentPatch(); // ブロック時は解析結果ごと破棄する（適用前提の状態を残さない）。
        var reason = best.Project.Id == project.Id
            ? $"このパッチは現在開いている「{project.DisplayName}」を含め、登録済みのどのプロジェクトともほとんど一致しません" +
              $"（最も一致率が高い候補でも{best.Ratio:P0}）。"
            : $"このパッチは現在開いている「{project.DisplayName}」とほとんど一致しません" +
              $"（最も近い「{best.Project.DisplayName}」でも一致率{best.Ratio:P0}）。";
        CenterError = GraftIssue.Of(
            ErrorCode.E303,
            reason +
            "貼り付け間違いの可能性があります。適用したいプロジェクトをサイドバーで選び直すか、" +
            "パッチの内容（AIとの会話）を確認してから、もう一度貼り付けてください。");
        State = CenterPaneState.Error;
        return false;
    }

    /// <summary>
    /// 最有力候補が現在のプロジェクトと異なる場合の確認。3択（切替／現在のプロジェクトのまま続行／
    /// キャンセル）で尋ねる。「現在のプロジェクトのまま続行」を選べるのは、確認ダイアログを
    /// 経由した利用者の明示的な判断であり、ProjectMatcher自体を無効化することとは異なるため
    /// （3.3の「無効化できてはならない」は、確認を経ずに素通りさせる設定を作らないことを指す）。
    /// </summary>
    private async Task<bool> ConfirmProjectSwitchAsync(Project project, ProjectMatchOutcome outcome)
    {
        var best = outcome.Best!;
        var message = outcome.Decision == ProjectMatchDecision.AutoSelected
            ? $"このパッチは「{best.Project.DisplayName}」の内容と一致率{best.Ratio:P0}で一致しています。" +
              $"現在開いているのは「{project.DisplayName}」です。\n\n" +
              $"「{best.Project.DisplayName}」に切り替えて解析を続けますか？"
            : BuildAmbiguousMatchMessage(project, outcome);

        var choice = await _dialogs.ConfirmThreeWayAsync(
            "別のプロジェクトの可能性があります",
            message,
            $"「{best.Project.DisplayName}」に切り替える",
            $"「{project.DisplayName}」のまま続行する").ConfigureAwait(true);

        if (choice is null)
        {
            // キャンセル。貼り付け間違いか判断がつかない状況で強行させたくないため、
            // どちらのプロジェクトにも適用せず解析結果ごと破棄する。
            DiscardCurrentPatch();
            return false;
        }

        if (choice == true)
        {
            var item = ProjectPane.Items.FirstOrDefault(i => i.Project.Id == best.Project.Id);
            if (item is not null)
            {
                // ProjectPane.SelectedItemのsetterはOnProjectSelectedを同期的に呼び出し、
                // その中でDiscardCurrentPatch（_currentPatch = null）が実行される。
                // 退避しておいたパッチを直後に復元することで、切り替え後も同じパッチで
                // ドライランを継続できるようにする。
                var keep = _currentPatch;
                ProjectPane.SelectedItem = item; // 8.10: 勝手な無音切替ではなく、確認済みの明示的な切替として行う。
                _currentPatch = keep;
            }
        }
        // choice == false: 現在のプロジェクトのまま続行する（何もしない）。

        var resolved = ProjectPane.SelectedItem?.Project ?? project;
        MarkMatchEvaluated(resolved);
        return true;
    }

    /// <summary>
    /// 50〜90%（NeedsConfirmation）で最有力候補が現在のプロジェクトと異なる場合のメッセージ。
    /// AutoSelectedと違い判定に確信が持てないケースのため、上位候補の一致率を併記し、
    /// 利用者が自分で見比べて判断できるようにする。
    /// </summary>
    private static string BuildAmbiguousMatchMessage(Project project, ProjectMatchOutcome outcome)
    {
        var best = outcome.Best!;
        var lines = outcome.Candidates
            .OrderByDescending(c => c.Ratio)
            .Take(3)
            .Select(c => $"・{c.Project.DisplayName}: 一致率{c.Ratio:P0}");

        return "パッチの内容から、対象プロジェクトを確実には判定できませんでした。" + Environment.NewLine + Environment.NewLine +
               string.Join(Environment.NewLine, lines) + Environment.NewLine + Environment.NewLine +
               $"現在開いているのは「{project.DisplayName}」です。最も近い「{best.Project.DisplayName}」に切り替えますか？";
    }
}
