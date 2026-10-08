using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TurulMC.Launcher.Avalonia.Views;

/// <summary>A CommunityToolkit-től független, pici MVVM alap — kevesebb
/// függőség, és pontosan azt csinálja, amire itt szükség van.</summary>
public abstract class PropertyChangedBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Raise<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
