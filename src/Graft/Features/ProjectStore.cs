using System.IO;
using System.Security.Cryptography;
using System.Text;
using Graft.Core;
using Graft.Infra;

namespace Graft.Features;

/// <summary>
/// projects.json の読み書きと、登録・検証・並べ替えといったプロジェクト管理の
/// 基本操作を提供する。仕様書3.1・3.2に対応する。
/// </summary>
public sealed class ProjectStore
{
    private readonly AppPaths _paths;
    private readonly JsonFileStore _store;

    public ProjectStore(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _store = new JsonFileStore();
    }

    /// <summary>
    /// projects.json を読み込む。ファイルが存在しない場合や破損している場合は
    /// <see cref="JsonFileStore"/> の共通復旧手順（13.1章）に従い、既定値
    /// （空一覧）から再生成する。
    /// </summary>
    public async Task<GraftResult<IReadOnlyList<Project>>> LoadAsync(CancellationToken ct = default)
    {
        var result = await _store
            .ReadWithRecoveryAsync(_paths.ProjectsFilePath, static () => new ProjectCatalog(), JsonFileStore.DefaultOptions, ct)
            .ConfigureAwait(false);
        return GraftResult<IReadOnlyList<Project>>.Ok(result.Value.Projects, result.Issues);
    }

    /// <summary>
    /// projects.json を書き込む。<see cref="Project.IsDisconnected"/> は起動時の検証で
    /// 都度算出する実行時のみの値であるため、保存前に常に false へ戻してから書き出す。
    /// </summary>
    public async Task<GraftResult<bool>> SaveAsync(IReadOnlyList<Project> projects, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(projects);
        var persisted = projects.Select(p => p.IsDisconnected ? p with { IsDisconnected = false } : p).ToList();
        var catalog = new ProjectCatalog { Projects = persisted };
        await _store.WriteAsync(_paths.ProjectsFilePath, catalog, JsonFileStore.DefaultOptions, ct).ConfigureAwait(false);
        return GraftResult<bool>.Ok(true);
    }

    /// <summary>
    /// ルートパスから決定的にIDを生成する。正規化（絶対パス化 → 区切りを "/" →
    /// 末尾スラッシュ除去 → 小文字化）した文字列のSHA-256先頭6桁に "p_" を付ける。
    /// フォルダ名を変更してもルートパスが同じであれば同一IDになる。
    /// </summary>
    public static string CreateId(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var normalized = NormalizeRootForHash(root);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return $"p_{hex[..6]}";
    }

    /// <summary>
    /// フォルダから新規登録する。既に同じルートが登録済みの場合は、そのプロジェクトの
    /// 最終使用日時と接続状態のみ更新して返す（重複登録はしない）。
    /// </summary>
    public async Task<GraftResult<Project>> RegisterAsync(string root, string? name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return GraftResult<Project>.Fail(ErrorCode.E201, "ルートパスが空です。", path: root);
        }

        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(root);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return GraftResult<Project>.Fail(ErrorCode.E201, $"不正なパスです: {ex.Message}", path: root);
        }

        var loaded = await LoadAsync(ct).ConfigureAwait(false);
        var projects = loaded.Value.ToList();
        var project = BuildOrUpdateProject(projects, fullRoot, name);
        await SaveAsync(projects, ct).ConfigureAwait(false);
        return GraftResult<Project>.Ok(project, loaded.Issues);
    }

    /// <summary>
    /// プロジェクトペイン改善: 指定プロジェクトの任意のフィールドを書き換える汎用の更新API。
    /// ピン留めの切替・表示名の変更・タグの編集はいずれも「1件を読み込み、書き換えて、
    /// 保存し直す」という同じ形をしているため、個々に専用メソッドを作らずここへ集約する。
    /// <paramref name="mutate"/> はrecordの<c>with</c>式で新しいProjectを組み立てて返すことを
    /// 想定する（Rootの変更でIdまで変える必要がある「場所を変更」だけは、back/配下の履歴フォルダの
    /// 移動が別途必要なため<see cref="RelocateAsync"/>という専用メソッドに分けている）。
    /// </summary>
    public async Task<GraftResult<Project>> UpdateAsync(
        string projectId, Func<Project, Project> mutate, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(mutate);

        var loaded = await LoadAsync(ct).ConfigureAwait(false);
        var projects = loaded.Value.ToList();
        var index = projects.FindIndex(p => p.Id == projectId);
        if (index < 0)
        {
            return GraftResult<Project>.Fail(ErrorCode.E201, "プロジェクトが見つかりません", path: projectId);
        }

        var updated = mutate(projects[index]);
        projects[index] = updated;
        await SaveAsync(projects, ct).ConfigureAwait(false);
        return GraftResult<Project>.Ok(updated, loaded.Issues);
    }

    /// <summary>
    /// プロジェクトペイン改善（利用者からの明示的な要望）: プロジェクトをGraftの一覧
    /// （projects.jsonのエントリ）から削除する。
    ///
    /// 【安全性についての設計判断（必ず守ること）】 ここでは<paramref name="projectId"/>から
    /// 導出できる back/&lt;projectId&gt;/ という「Graft自身が作ったバックアップフォルダ」以外には
    /// 一切触れない。利用者が実際に作業している元のプロジェクトフォルダ（Project.Root）は
    /// このメソッドの引数にすら含まれておらず、参照すらしていない
    /// （projects.jsonからエントリを取り除くだけで、Directory.Delete等のファイルシステム操作は
    /// バックアップフォルダに対してのみ、<paramref name="deleteHistory"/>がtrueのときだけ行う）。
    /// </summary>
    /// <param name="deleteHistory">
    /// trueならback/&lt;projectId&gt;/配下の変更履歴（バックアップ・history.jsonl）も削除する。
    /// falseなら履歴フォルダはそのまま残す（同じフォルダを後で再登録すると、CreateIdがパスから
    /// 決定的に決まるため同じIdになり、履歴が自動的に復活する。<see cref="CreateId"/>参照）。
    /// </param>
    public async Task<GraftResult<bool>> RemoveAsync(string projectId, bool deleteHistory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var loaded = await LoadAsync(ct).ConfigureAwait(false);
        var projects = loaded.Value.ToList();
        var index = projects.FindIndex(p => p.Id == projectId);
        if (index < 0)
        {
            return GraftResult<bool>.Fail(ErrorCode.E201, "プロジェクトが見つかりません", path: projectId);
        }

        projects.RemoveAt(index);
        await SaveAsync(projects, ct).ConfigureAwait(false);

        var issues = new List<GraftIssue>(loaded.Issues);
        if (deleteHistory)
        {
            var backupDir = _paths.GetProjectBackupDirectory(projectId);
            TryDeleteDirectory(backupDir, issues);
        }

        return GraftResult<bool>.Ok(true, issues);
    }

    /// <summary>
    /// プロジェクトペイン改善（利用者からの明示的な要望）: フォルダの移動・リネームで
    /// <see cref="Project.IsDisconnected"/> になったプロジェクトを、登録し直すのではなく
    /// 同じプロジェクトのまま新しい場所へ結び付け直す。
    ///
    /// 【Idが変わる問題への対応】 <see cref="CreateId"/> はRootパスから決定的に算出するため、
    /// Rootを変えるとId自体も変わる。back/&lt;projectId&gt;/ 配下の履歴はIdをキーに保存されている
    /// （<see cref="Graft.Infra.AppPaths.GetProjectBackupDirectory"/>・<see cref="Graft.Core.RevisionIndex"/>
    /// 参照）ため、Idだけ変えて履歴フォルダを放置すると「新しいIdの履歴フォルダが存在しない
    /// ＝復元不可なプロジェクト」に見えてしまう。これを避けるため、Idが変わる場合は
    /// back/&lt;旧Id&gt;/ を back/&lt;新Id&gt;/ へ丸ごと移動し、履歴が旧Idに取り残されないようにする
    /// （新規登録＝別プロジェクト扱いになり履歴が切り離される、という元の不具合の解消が目的）。
    /// </summary>
    public async Task<GraftResult<Project>> RelocateAsync(string projectId, string newRoot, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        if (string.IsNullOrWhiteSpace(newRoot))
        {
            return GraftResult<Project>.Fail(ErrorCode.E201, "新しい場所が指定されていません。", path: newRoot);
        }

        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(newRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return GraftResult<Project>.Fail(ErrorCode.E201, $"不正なパスです: {ex.Message}", path: newRoot);
        }

        if (!Directory.Exists(fullRoot))
        {
            return GraftResult<Project>.Fail(ErrorCode.E201, "指定したフォルダが見つかりません。", path: fullRoot);
        }

        var loaded = await LoadAsync(ct).ConfigureAwait(false);
        var projects = loaded.Value.ToList();
        var index = projects.FindIndex(p => p.Id == projectId);
        if (index < 0)
        {
            return GraftResult<Project>.Fail(ErrorCode.E201, "プロジェクトが見つかりません", path: projectId);
        }

        var newId = CreateId(fullRoot);
        // 選んだフォルダが既に別プロジェクトとして登録されている場合は重複登録にしない
        // （選んだフォルダ＝自分自身の現在のRootを選び直しただけの場合はnewId==projectIdになり、
        // この条件には該当しない）。
        if (newId != projectId && projects.Any(p => p.Id == newId))
        {
            return GraftResult<Project>.Fail(ErrorCode.E209, "選んだフォルダは既に別のプロジェクトとして登録されています。", path: fullRoot);
        }

        var issues = new List<GraftIssue>(loaded.Issues);
        if (newId != projectId)
        {
            MoveBackupDirectory(projectId, newId, issues);
        }

        var updated = projects[index] with
        {
            Id = newId,
            Root = fullRoot,
            LastUsedAt = DateTimeOffset.Now,
            IsDisconnected = false,
        };
        projects[index] = updated;
        await SaveAsync(projects, ct).ConfigureAwait(false);
        return GraftResult<Project>.Ok(updated, issues);
    }

    /// <summary>
    /// back/&lt;fromId&gt;/ を back/&lt;toId&gt;/ へ移動する。移動先が既に存在する場合
    /// （理論上は起こらないはずだが、念のため）は安全側に倒して移動をせず、履歴が旧Idの
    /// フォルダに残ったままであることを警告として伝える（例外にはしない。仕様書16章の方針）。
    /// </summary>
    private void MoveBackupDirectory(string fromId, string toId, List<GraftIssue> issues)
    {
        var oldDir = _paths.GetProjectBackupDirectory(fromId);
        if (!Directory.Exists(oldDir))
        {
            return; // 移動対象の履歴が無い（まだ一度も適用していない等）。何もしなくてよい。
        }

        var newDir = _paths.GetProjectBackupDirectory(toId);
        if (Directory.Exists(newDir))
        {
            issues.Add(GraftIssue.Of(
                ErrorCode.E402,
                $"移動先の履歴フォルダ（{newDir}）が既に存在するため、履歴フォルダ（{oldDir}）は移動しませんでした。手動でご確認ください。",
                path: oldDir,
                severity: Severity.Warning));
            return;
        }

        try
        {
            Directory.Move(oldDir, newDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            issues.Add(GraftIssue.Of(
                ErrorCode.E402,
                $"変更履歴フォルダの移動に失敗しました。履歴は元の場所（{oldDir}）に残っています。{ExceptionMessages.Describe(ex)}",
                path: oldDir,
                severity: Severity.Warning));
        }
    }

    /// <summary>
    /// back/&lt;projectId&gt;/ を再帰削除する。存在しない場合は何もしない。失敗しても例外は
    /// 投げず、Warningとして issues へ積むだけにとどめる（仕様書16章「例外を投げず穏当に扱う」）。
    /// </summary>
    private static void TryDeleteDirectory(string directory, List<GraftIssue> issues)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            issues.Add(GraftIssue.Of(
                ErrorCode.E402,
                $"変更履歴フォルダの削除に失敗しました。プロジェクトの登録は削除済みです。{ExceptionMessages.Describe(ex)}",
                path: directory,
                severity: Severity.Warning));
        }
    }

    /// <summary>
    /// 起動時の検証。ルートフォルダが存在しないプロジェクトは削除せず
    /// <see cref="Project.IsDisconnected"/> を立てるだけにとどめる（仕様書3.2）。
    /// </summary>
    public Task<GraftResult<IReadOnlyList<Project>>> ValidateAsync(
        IReadOnlyList<Project> projects, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(projects);
        var validated = new List<Project>(projects.Count);
        var issues = new List<GraftIssue>();

        foreach (var project in projects)
        {
            ct.ThrowIfCancellationRequested();
            var exists = Directory.Exists(project.Root);
            validated.Add(project with { IsDisconnected = !exists });
            if (!exists)
            {
                issues.Add(GraftIssue.Of(
                    ErrorCode.E404,
                    detail: $"プロジェクト「{project.DisplayName}」のルート（{project.Root}）が見つからないため未接続にしました。",
                    path: project.Root,
                    severity: Severity.Warning));
            }
        }

        return Task.FromResult(GraftResult<IReadOnlyList<Project>>.Ok(validated, issues));
    }

    /// <summary>
    /// ピン留めを先頭に、次に最終使用日時の降順で並べる（仕様書3.2）。
    /// 上位9件が数字キーショートカットの割り当て対象になる。
    /// </summary>
    public static IReadOnlyList<Project> Sort(IEnumerable<Project> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        return projects
            .OrderByDescending(p => p.Pinned)
            .ThenByDescending(p => p.LastUsedAt)
            .ToList();
    }

    /// <summary>
    /// nextRevision を実体（back/ 配下）の最大リビジョン+1へ補正する（仕様書13.1）。
    /// 既に十分大きい場合は何もしない。実体の最大値は <c>RevisionStore</c> 側から渡す。
    /// </summary>
    public static Project ReconcileRevision(Project project, int actualMaxRevision)
    {
        ArgumentNullException.ThrowIfNull(project);
        var minimumNext = actualMaxRevision + 1;
        return project.NextRevision < minimumNext ? project with { NextRevision = minimumNext } : project;
    }

    /// <summary>
    /// 不具合2対応: 指定プロジェクトの nextRevision を1つ進めてprojects.jsonへ永続化し、
    /// 消費前の値（今回の適用で実際に使う番号）を返す。
    ///
    /// 呼び出しタイミング: 適用（ApplyEngine.ApplyAsync）を試みた直後に、成功・失敗を問わず
    /// 呼ぶ想定（仕様書6章）。BackupManager.BeginAsyncはctx.Revisionの番号でバックアップ
    /// フォルダを先に作成するため、二重適用検知・fatalな検証エラーによる早期リターンを除き、
    /// 適用が失敗してもディスク上には既にその番号のフォルダが作られている場合がある
    /// （RollbackAsyncはファイル内容を元に戻すだけでフォルダ自体は削除しない。6.3の
    /// 中断復帰検出が次回起動時にこのフォルダを拾えるようにするための挙動）。
    /// ここで番号を消費せずに同じ番号で再適用すると、同一リビジョン番号のフォルダが
    /// タイムスタンプ違いでもう1つ作られてしまい、世代管理・履歴一覧が
    /// 一方のフォルダを見失う実害がある。そのため失敗時も一律に番号を進める設計とし、
    /// 早期リターン（フォルダが作られない場合）で番号が1つ「無駄」になることは許容する
    /// （13.1「フォルダが後で削除されても番号を再利用しない」と同じ、欠番を許容する方針）。
    ///
    /// 複数プロジェクトの独立性: projectId で該当プロジェクトのみを更新するため、
    /// 他プロジェクトのnextRevisionには影響しない。
    ///
    /// 対象プロジェクトが見つからない場合（並行してプロジェクトが削除された等）は
    /// 何もせず、渡されたプロジェクトの現在値をそのまま返す。
    /// </summary>
    public async Task<GraftResult<int>> ConsumeNextRevisionAsync(string projectId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var loaded = await LoadAsync(ct).ConfigureAwait(false);
        var projects = loaded.Value.ToList();
        var index = projects.FindIndex(p => p.Id == projectId);
        if (index < 0)
        {
            return GraftResult<int>.Fail(ErrorCode.E201, "プロジェクトが見つかりません", path: projectId);
        }

        var consumed = projects[index].NextRevision;
        projects[index] = projects[index] with { NextRevision = consumed + 1 };
        await SaveAsync(projects, ct).ConfigureAwait(false);
        return GraftResult<int>.Ok(consumed, loaded.Issues);
    }

    /// <summary>
    /// 不具合対応: <see cref="ConsumeNextRevisionAsync"/> で消費したものの、結局リビジョンとして
    /// 記録しなかった番号（例: 「ここまで戻す」で変更が1件も無く、空リビジョン抑止が働いた場合）を
    /// 安全に返却する。呼び出し元が消費した <paramref name="revision"/> を再び nextRevision へ戻し、
    /// 次回の消費で同じ番号が使われるようにすることで、履歴の欠番（r7 → r9 のように見える見た目）を防ぐ。
    ///
    /// 安全条件（<c>NextRevision == revision + 1</c> のときだけ返却する）について:
    /// 消費してから返却するまでの間に、同じプロジェクトへの別の操作（並行するもう1つの
    /// ConsumeNextRevisionAsync呼び出しや、通常の適用など）が既にnextRevisionを進めていた場合、
    /// ここで無条件に revision を書き戻すと、次にConsumeNextRevisionAsyncが返す番号が
    /// その別操作が既に使い始めている番号と重複してしまう。番号が重複すると、
    /// back/{projectId}/配下のバックアップフォルダが同一番号でタイムスタンプ違いに2つ作られ、
    /// 履歴一覧・世代管理が一方のフォルダを見失う実害につながる
    /// （<see cref="ConsumeNextRevisionAsync"/>のコメントで説明している「番号を消費せずに
    /// 同じ番号で再適用してはいけない」理由と同じ種類の衝突）。
    /// そのため、「自分が消費した番号がまだ他の誰にも使われていない
    /// （nextRevisionがちょうど1つだけ進んだ状態のまま）」ことを確認できたときに限り、
    /// 安全に巻き戻す。既に他の操作が番号を進めていた場合は、欠番を許容してここでは何もしない
    /// （13.1「フォルダが後で削除されても番号を再利用しない」と同じ、欠番を許容する既存方針に合わせる）。
    ///
    /// 戻り値: 実際に巻き戻せた場合は true、安全条件を満たさず何もしなかった場合は false を返す
    /// （どちらも失敗ではないため IsSuccess=true）。対象プロジェクトが見つからない場合のみ失敗を返す。
    /// </summary>
    public async Task<GraftResult<bool>> ReleaseRevisionAsync(string projectId, int revision, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var loaded = await LoadAsync(ct).ConfigureAwait(false);
        var projects = loaded.Value.ToList();
        var index = projects.FindIndex(p => p.Id == projectId);
        if (index < 0)
        {
            return GraftResult<bool>.Fail(ErrorCode.E201, "プロジェクトが見つかりません", path: projectId);
        }

        if (projects[index].NextRevision != revision + 1)
        {
            // 既に他の操作がnextRevisionを進めている（＝revision+1から動いている）ため、
            // ここで戻すと番号の重複を招く。何もせず、返却しなかったことをfalseで伝える。
            return GraftResult<bool>.Ok(false, loaded.Issues);
        }

        projects[index] = projects[index] with { NextRevision = revision };
        await SaveAsync(projects, ct).ConfigureAwait(false);
        return GraftResult<bool>.Ok(true, loaded.Issues);
    }

    private static Project BuildOrUpdateProject(List<Project> projects, string fullRoot, string? name)
    {
        var id = CreateId(fullRoot);
        var isDisconnected = !Directory.Exists(fullRoot);
        var now = DateTimeOffset.Now;
        var index = projects.FindIndex(p => p.Id == id);

        if (index >= 0)
        {
            var updated = projects[index] with { Root = fullRoot, LastUsedAt = now, IsDisconnected = isDisconnected };
            projects[index] = updated;
            return updated;
        }

        var created = new Project
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? DeriveDefaultName(fullRoot) : name,
            Root = fullRoot,
            LastUsedAt = now,
            NextRevision = 1,
            IsDisconnected = isDisconnected,
        };
        projects.Add(created);
        return created;
    }

    private static string DeriveDefaultName(string fullRoot)
    {
        var trimmed = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    private static string NormalizeRootForHash(string root)
    {
        var full = Path.GetFullPath(root);
        var withSlashes = full.Replace('\\', '/');
        var trimmed = withSlashes.Length > 1 ? withSlashes.TrimEnd('/') : withSlashes;

        // 仕様書v2.1 3章: パスの比較規則はプラットフォームへ委ねる。Windowsは大文字小文字を
        // 無視するため小文字へ正規化し、区別するLinuxではそのまま使う。ここで一律に
        // 小文字化すると、Linuxで大文字小文字だけが異なる別フォルダが同一プロジェクトIDになる。
        return OperatingSystem.IsWindows() ? trimmed.ToLowerInvariant() : trimmed;
    }
}
