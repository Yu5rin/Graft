using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Graft.Core;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// ファイル単位の変更履歴機能: <see cref="RevisionStore.Filter"/>のファイルパス絞り込み
/// （<see cref="RevisionStore.EntryPathEquals"/>）の回帰テスト。実ファイルI/Oは使わず、
/// <see cref="RevisionSummary"/>を直接組み立てて検証する（HistoryPaneViewModel経由の
/// エンドツーエンドの検証はGraft.UiTests側のシナリオテストで行う）。
/// </summary>
public class RevisionStoreFileFilterTests
{
    [Fact(DisplayName = "対象ファイルを含むリビジョンだけが残る")]
    public void 対象ファイルを含むリビジョンだけ残る()
    {
        var r1 = CreateSummary(1, ("a.txt", EntryOperation.Modify));
        var r2 = CreateSummary(2, ("b.txt", EntryOperation.Modify));
        var r3 = CreateSummary(3, ("a.txt", EntryOperation.Modify), ("c.txt", EntryOperation.Create));

        var filtered = RevisionStore.Filter(new[] { r1, r2, r3 }, null, null, null, null, "a.txt").ToList();

        filtered.Select(r => r.Manifest.Revision).Should().BeEquivalentTo(new[] { 1, 3 },
            "a.txtを変更したリビジョン（r1・r3）だけが残るはず");
    }

    [Fact(DisplayName = "対象ファイルの履歴が無ければ0件になる")]
    public void 履歴が無ければ0件になる()
    {
        var r1 = CreateSummary(1, ("a.txt", EntryOperation.Modify));

        var filtered = RevisionStore.Filter(new[] { r1 }, null, null, null, null, "untouched.txt").ToList();

        filtered.Should().BeEmpty();
    }

    [Fact(DisplayName = "filePathを指定しなければ従来どおり絞り込まれない")]
    public void filePath未指定なら絞り込まれない()
    {
        var r1 = CreateSummary(1, ("a.txt", EntryOperation.Modify));
        var r2 = CreateSummary(2, ("b.txt", EntryOperation.Modify));

        var filtered = RevisionStore.Filter(new[] { r1, r2 }, null, null, null, null).ToList();

        filtered.Should().HaveCount(2, "filePathを省略した既存の呼び出し（type/keyword/日付絞り込み）の挙動を変えてはいけない");
    }

    [Fact(DisplayName = "リネーム前の旧パス（RenamedFrom）では一致しない（現在のパスのみで判定する割り切り）")]
    public void リネーム前の旧パスでは一致しない()
    {
        var renamed = CreateSummaryWithEntries(1, new RevisionEntry
        {
            Path = "new.txt",
            Operation = EntryOperation.Rename,
            RenamedFrom = "old.txt",
        });

        // 現在のパス（リネーム後）でなら見つかる。
        RevisionStore.Filter(new[] { renamed }, null, null, null, null, "new.txt").Should().ContainSingle();

        // リネーム前の旧パスでは追跡しない、という仕様上の割り切り。
        RevisionStore.Filter(new[] { renamed }, null, null, null, null, "old.txt").Should().BeEmpty();
    }

    [Fact(DisplayName = "他の絞り込み（種別・期間）と複合しても正しく絞り込める")]
    public void 他の絞り込みと複合できる()
    {
        var r1 = CreateSummary(1, ("a.txt", EntryOperation.Modify), type: "fix");
        var r2 = CreateSummary(2, ("a.txt", EntryOperation.Modify), type: "feat");

        var filtered = RevisionStore.Filter(new[] { r1, r2 }, null, "fix", null, null, "a.txt").ToList();

        filtered.Select(r => r.Manifest.Revision).Should().BeEquivalentTo(new[] { 1 });
    }

    private static RevisionSummary CreateSummary(int revision, (string Path, string Operation) entry, string? type = null)
        => CreateSummaryWithEntries(revision, new RevisionEntry { Path = entry.Path, Operation = entry.Operation }, type: type);

    private static RevisionSummary CreateSummary(
        int revision, (string Path, string Operation) entry1, (string Path, string Operation) entry2, string? type = null)
        => CreateSummaryWithEntries(
            revision,
            new[]
            {
                new RevisionEntry { Path = entry1.Path, Operation = entry1.Operation },
                new RevisionEntry { Path = entry2.Path, Operation = entry2.Operation },
            },
            type);

    private static RevisionSummary CreateSummaryWithEntries(int revision, RevisionEntry entry, string? type = null)
        => CreateSummaryWithEntries(revision, new[] { entry }, type);

    private static RevisionSummary CreateSummaryWithEntries(int revision, IReadOnlyList<RevisionEntry> entries, string? type = null)
        => new()
        {
            Manifest = new RevisionManifest
            {
                Revision = revision,
                ProjectId = "test-project",
                AppliedAt = DateTimeOffset.Now,
                Type = type,
                Entries = entries,
            },
            FolderPath = $"/tmp/fake/r{revision}",
            IsRestorable = true,
        };
}
