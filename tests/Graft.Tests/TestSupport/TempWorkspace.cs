using System;
using System.IO;
using System.Text;

namespace Graft.Tests.TestSupport;

/// <summary>
/// テストごとに専用の一時ディレクトリを作成し、Dispose時に再帰的に削除するヘルパ。
/// PathGuard・FileTextIO・SafeFileWriter等、実ファイルI/Oを伴うテストで共通利用する。
/// </summary>
public sealed class TempWorkspace : IDisposable
{
    /// <summary>作成された一時ディレクトリの絶対パス。</summary>
    public string RootPath { get; }

    public TempWorkspace()
    {
        RootPath = Path.Combine(Path.GetTempPath(), "graft-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
    }

    /// <summary>ルートからの相対パス（"/" 区切り可）を、このOSのディレクトリ区切りに変換した絶対パスへ解決する。</summary>
    public string Combine(string relativePath)
        => Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>UTF-8（BOMなし）でテキストファイルを書き込む。親ディレクトリが無ければ作成する。</summary>
    public string WriteText(string relativePath, string content)
        => WriteBytes(relativePath, new UTF8Encoding(false).GetBytes(content));

    /// <summary>
    /// バイト列をそのまま書き込む。BOM付き・Shift_JIS等、バイト単位で厳密に再現したい
    /// フィクスチャはテキストファイルとして置かず、この方式で都度生成する。
    /// </summary>
    public string WriteBytes(string relativePath, byte[] content)
    {
        var full = Combine(relativePath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(full, content);
        return full;
    }

    /// <summary>ルート配下のファイルをバイト列として読み込む。</summary>
    public byte[] ReadBytes(string relativePath) => File.ReadAllBytes(Combine(relativePath));

    /// <summary>ディレクトリを作成し、その絶対パスを返す。</summary>
    public string CreateDirectory(string relativePath)
    {
        var full = Combine(relativePath);
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>
    /// ディレクトリへのシンボリックリンクを作成する。ルート外へのパス解決を検証する
    /// テスト（PathGuardの13章要件）で使う。Linux上での実行を前提とする。
    /// </summary>
    public string CreateDirectorySymlink(string linkRelativePath, string targetAbsolutePath)
    {
        var linkFull = Combine(linkRelativePath);
        var dir = Path.GetDirectoryName(linkFull);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        Directory.CreateSymbolicLink(linkFull, targetAbsolutePath);
        return linkFull;
    }

    /// <summary>指定ファイルへ読み取り専用属性を設定・解除する。</summary>
    public void SetReadOnly(string relativePath, bool readOnly)
    {
        var full = Combine(relativePath);
        var attrs = File.GetAttributes(full);
        File.SetAttributes(full, readOnly ? attrs | FileAttributes.ReadOnly : attrs & ~FileAttributes.ReadOnly);
    }

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(RootPath)) return;
            ClearReadOnlyRecursively(RootPath);
            Directory.Delete(RootPath, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗はテスト結果に影響させないベストエフォート。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }

    /// <summary>
    /// 読み取り専用属性を再帰的に解除する。シンボリックリンク配下（ルート外を指す
    /// 可能性がある）へは決して降りない。<see cref="Directory.EnumerateFiles"/> 系は
    /// リンクを辿ってしまうため、ここでは <see cref="FileAttributes.ReparsePoint"/> を
    /// 手動で判定しながら再帰する。
    /// </summary>
    private static void ClearReadOnlyRecursively(string dir)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                var attrs = File.GetAttributes(entry);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attrs & FileAttributes.Directory) != 0)
                {
                    ClearReadOnlyRecursively(entry);
                    continue;
                }

                if ((attrs & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(entry, attrs & ~FileAttributes.ReadOnly);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
