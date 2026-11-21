namespace Graft.Core.Vba;

/// <summary>
/// VBA プロジェクトの状態値の判定（仕様書_VBA連携 5.4）。COM から読んだ整数を解釈するだけの純ロジック。
/// </summary>
public static class VbaProjectState
{
    /// <summary><c>vbext_pp_locked</c>。<c>VBProject.Protection</c> がこの値ならパスワードでロックされている。</summary>
    public const int ProtectionLocked = 1;

    /// <summary>
    /// <c>vbext_vm_Design</c>。<c>VBProject.Mode</c> の値は VBIDE の列挙体で Run=0、Break=1、Design=2。
    /// <para>
    /// 【仕様書との食い違い】 仕様書の 5.4 は「<c>vbext_vm_Design = 1</c>」と書いているが、VBIDE の
    /// <c>vbext_VBAMode</c> の定義では Design が 2 で、1 は中断（Break）。1 をデザインモードとして扱うと、
    /// 中断中に書き込みを通してしまい、デザインモードの通常時は逆に E813 で止まってしまうので、
    /// 定義どおりの 2 を採用した。この値の実機での確認は付録B・10.3-9 の項目のまま残す。
    /// </para>
    /// </summary>
    public const int ModeDesign = 2;

    /// <summary>パスワードでロックされているか。</summary>
    public static bool IsLocked(int protection) => protection == ProtectionLocked;

    /// <summary>書き込みしてよいモード（デザインモード）か。実行中・中断中なら false（E813）。</summary>
    public static bool IsDesignMode(int mode) => mode == ModeDesign;
}
