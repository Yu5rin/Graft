using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="INotifyPropertyChanged"/> の自前基底クラス。
/// MVVMフレームワーク（CommunityToolkit.Mvvm等）は導入しない方針（附録A.3）のため、
/// ViewModelはすべてこのクラスを継承する。
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// フィールドに値を設定し、変化があった場合のみ <see cref="PropertyChanged"/> を発火する。
    /// </summary>
    /// <returns>値が変化した場合はtrue。</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>
    /// フィールドに値を設定し、変化があった場合に <see cref="PropertyChanged"/> を発火する。
    /// 設定後に副作用（依存プロパティの再計算等）を実行したい場合に使う。
    /// </summary>
    /// <returns>値が変化した場合はtrue。</returns>
    protected bool SetProperty<T>(ref T field, T value, Action onChanged, [CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName))
        {
            return false;
        }

        onChanged();
        return true;
    }

    /// <summary>指定したプロパティ名（省略時は呼び出し元プロパティ名）で変更通知を発火する。</summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
