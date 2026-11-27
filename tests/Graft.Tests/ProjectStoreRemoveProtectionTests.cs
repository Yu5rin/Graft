using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// <see cref="ProjectStore.RemoveAsync"/> が、projects.json への保存が実体へ反映されない状況（読み取り失敗の保護で
/// <see cref="JsonFileStore"/> が書き込みを黙って止めている状態）を成功として返さないことの確認。
/// 実機の「削除したのに一覧に残る」の診断用の守りで、原因そのものの修正ではない。
/// </summary>
public class ProjectStoreRemoveProtectionTests
{
    [Fact(DisplayName = "RemoveAsync: projects.json を読み取れず保存が止められている間は、成功にせず E402 で失敗し、履歴にも触れない")]
    public async Task 保存が止められているあいだは失敗を返す()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);
        var root = ws.CreateDirectory("proj");
        var id = (await store.RegisterAsync(root, "P")).Value.Id;
        var historyFile = Path.Combine(paths.GetProjectBackupDirectory(id), "history.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(historyFile)!);
        await File.WriteAllTextAsync(historyFile, "{\"revision\":1}\n");

        GraftResult<bool> result;
        // 共有を許さずに開いておく。.NET は Unix でも FileShare.None を（同一の .NET プロセス内で）守るため、
        // Windows の「他のアプリが開いている」と同じ読み取り失敗を、どちらの OS でも作れる。
        using (new FileStream(paths.ProjectsFilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await store.RemoveAsync(id, deleteHistory: true);
        }

        result.IsSuccess.Should().BeFalse("読めないファイルを既定値のまま保存したことにして、成功と返してはいけない");
        result.Issues.Should().Contain(i => i.Code == ErrorCode.E402);
        File.Exists(historyFile).Should().BeTrue("登録を外せていないので、履歴は消さない");

        // 保護は次に正しく読めた時点で外れる。ロックを外したあとは、登録も残ったまま、普通に削除できる。
        (await store.LoadAsync()).Value.Should().ContainSingle(p => p.Id == id, "登録は消えていない");
        (await store.RemoveAsync(id, deleteHistory: false)).IsSuccess.Should().BeTrue();
        (await store.LoadAsync()).Value.Should().NotContain(p => p.Id == id);
    }
}
