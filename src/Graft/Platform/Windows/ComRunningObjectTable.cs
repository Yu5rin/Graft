using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using Graft.Core.Vba;

namespace Graft.Platform.Windows;

/// <summary>
/// 実行中オブジェクトテーブル（ROT）の列挙（仕様書_VBA連携 5.2）。
/// <para>
/// 【<c>Marshal.BindToMoniker</c> を使わない理由】 BindToMoniker は、ブックが開いていないとき Excel を起動して
/// ファイルを開いてしまう。ROT を自分で列挙して、いま開いているものだけを取り出す。
/// </para>
/// <para>
/// 【<c>GetActiveObject</c> ではなく ROT を列挙する理由】 .NET 8 には <c>Marshal.GetActiveObject</c> が無い。
/// また ROT なら複数の Excel プロセスのブックをまとめて扱える。
/// </para>
/// <para>
/// 【32bit の Excel でも動く理由】 Excel はアウトプロセスの COM サーバーなので、Graft（x64）と bitness が違っても
/// プロセス間通信で動く。呼び出しは IDispatch の遅延バインディング（<c>dynamic</c>）で、型ライブラリの登録に
/// 依存しない（PIA を参照しない理由でもある）。ROT もプロセスをまたいで共有されるので bitness に関係なく見える。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ComRunningObjectTable
{
    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable runningObjectTable);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx bindContext);

    /// <summary>
    /// ROT のスナップショット。表示名が Excel のブックの拡張子で終わるモニカだけを持つ。
    /// 使い終わったら <see cref="Dispose"/> で、保持しているモニカと ROT を解放する。
    /// </summary>
    internal sealed class Snapshot : IDisposable
    {
        private readonly IRunningObjectTable _rot;
        private readonly IBindCtx _bindContext;
        private readonly List<IMoniker> _monikers;

        public Snapshot(IRunningObjectTable rot, IBindCtx bindContext, List<IMoniker> monikers, List<string> displayNames)
        {
            _rot = rot;
            _bindContext = bindContext;
            _monikers = monikers;
            DisplayNames = displayNames;
        }

        /// <summary>モニカの表示名（ファイルモニカならブックのフルパス）。<see cref="GetObject"/> の添字と対応する。</summary>
        public IReadOnlyList<string> DisplayNames { get; }

        /// <summary>
        /// 添字のモニカから、実行中のオブジェクト（Workbook）を取り出す。取り出したものは呼び出し側が
        /// <see cref="ComScope.Own"/> で所有すること。取れなかった（登録直後に閉じられたなど）ときは null。
        /// </summary>
        public object? GetObject(int index)
        {
            try
            {
                var hr = _rot.GetObject(_monikers[index], out var obj);
                return hr == 0 ? obj : null;
            }
            catch (COMException)
            {
                return null;
            }
        }

        public void Dispose()
        {
            foreach (var moniker in _monikers) ReleaseQuietly(moniker);
            _monikers.Clear();
            ReleaseQuietly(_bindContext);
            ReleaseQuietly(_rot);
        }
    }

    /// <summary>
    /// ROT を列挙して、ブックの拡張子で終わる表示名のモニカを集める。ROT が取れなければ空。
    /// 表示名の取得に失敗したモニカ（別プロセスが終了した直後など）は読み飛ばす。
    /// </summary>
    public static Snapshot Enumerate()
    {
        var monikers = new List<IMoniker>();
        var names = new List<string>();

        if (GetRunningObjectTable(0, out var rot) != 0 || rot is null)
        {
            throw new COMException("実行中オブジェクトテーブルを取得できませんでした。");
        }

        if (CreateBindCtx(0, out var bindContext) != 0 || bindContext is null)
        {
            ReleaseQuietly(rot);
            throw new COMException("バインドコンテキストを作成できませんでした。");
        }

        IEnumMoniker? enumerator = null;
        try
        {
            rot.EnumRunning(out enumerator);
            enumerator.Reset();
            var buffer = new IMoniker[1];
            while (enumerator.Next(1, buffer, IntPtr.Zero) == 0)
            {
                var moniker = buffer[0];
                buffer[0] = null!;
                string? displayName = null;
                try
                {
                    moniker.GetDisplayName(bindContext, null!, out displayName);
                }
                catch (COMException)
                {
                    // 読めないモニカは読み飛ばす。
                }

                // 表示名で先に絞る（VbaRotNames）。無関係なオブジェクトの GetObject を呼ばずに済む。
                if (VbaRotNames.IsWorkbookDisplayName(displayName))
                {
                    monikers.Add(moniker);
                    names.Add(displayName!);
                }
                else
                {
                    ReleaseQuietly(moniker);
                }
            }
        }
        catch
        {
            foreach (var moniker in monikers) ReleaseQuietly(moniker);
            ReleaseQuietly(bindContext);
            ReleaseQuietly(rot);
            throw;
        }
        finally
        {
            ReleaseQuietly(enumerator);
        }

        return new Snapshot(rot, bindContext, monikers, names);
    }

    private static void ReleaseQuietly(object? comObject)
    {
        if (comObject is null) return;
        try
        {
            if (Marshal.IsComObject(comObject)) Marshal.FinalReleaseComObject(comObject);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidComObjectException or COMException)
        {
            // 解放済み、または相手が居ない。
        }
    }
}
