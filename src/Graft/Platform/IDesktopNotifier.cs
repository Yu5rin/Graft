using System.Diagnostics;

namespace Graft.Platform;

/// <summary>
/// デスクトップ通知（仕様書9章「トレイ通知のみ」）を出す手段の抽象。
/// AvaloniaのTrayIconにはバルーン通知に相当するAPIが無いため、OSごとの通知手段へ委譲する。
/// </summary>
public interface IDesktopNotifier
{
    /// <summary>通知を表示する。利用できない環境では何もしない（例外は投げない）。</summary>
    void Notify(string title, string text);
}

/// <summary>通知手段が無い環境向けの、何もしない実装。</summary>
public sealed class NullDesktopNotifier : IDesktopNotifier
{
    public void Notify(string title, string text)
    {
        // 何もしない。
    }
}

/// <summary>
/// Linuxのデスクトップ通知。<c>notify-send</c>（libnotify）へ委譲する。
/// DBusを直接叩く実装は依存を増やす（附録A.2 依存最小化）ため採らない。
/// <c>notify-send</c>が入っていない環境では静かに失敗し、通知が出ないだけになる。
/// </summary>
public sealed class LinuxDesktopNotifier : IDesktopNotifier
{
    public void Notify(string title, string text)
    {
        try
        {
            var info = new ProcessStartInfo("notify-send")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add("--app-name=Graft");
            info.ArgumentList.Add(title);
            info.ArgumentList.Add(text);
            using var process = Process.Start(info);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // notify-send が無い環境では通知を諦める（機能の縮退。操作は妨げない）。
        }
    }
}
