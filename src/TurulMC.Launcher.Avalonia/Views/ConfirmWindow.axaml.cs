using Avalonia.Controls;

namespace TurulMC.Launcher.Avalonia.Views;

/// <summary>
/// Egyszerű választó-dialógus (pl. bezárás-megerősítéshez).
/// Eredmény: a választott gomb indexe, vagy -1 (ablak bezárva máshogy).
/// </summary>
public partial class ConfirmWindow : Window
{
    private int _result = -1;

    public ConfirmWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Megjeleníti a dialógust; az <c>options</c> felirataiból lesznek a gombok.
    /// Az első gomb arany (elsődleges), a többi semleges.
    /// </summary>
    public static async Task<int> AskAsync(
        Window owner, string title, string message, params string[] options)
    {
        var dialog = new ConfirmWindow
        {
            Title = title
        };
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;

        var gold = (global::Avalonia.Media.IBrush?)dialog.FindResource("TurulGoldBrush");
        var bg = (global::Avalonia.Media.IBrush?)dialog.FindResource("TurulBgBrush");
        var surface2 = (global::Avalonia.Media.IBrush?)dialog.FindResource("TurulSurface2Brush");
        var text = (global::Avalonia.Media.IBrush?)dialog.FindResource("TurulTextBrush");
        var border = (global::Avalonia.Media.IBrush?)dialog.FindResource("TurulBorderBrush");

        for (var i = 0; i < options.Length; i++)
        {
            var index = i;
            var primary = i == 0;
            var button = new Button
            {
                Content = options[i],
                HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
                Padding = new global::Avalonia.Thickness(14, 9),
                CornerRadius = new global::Avalonia.CornerRadius(8),
                FontSize = 13,
                FontWeight = primary ? global::Avalonia.Media.FontWeight.SemiBold
                                     : global::Avalonia.Media.FontWeight.Normal,
                Background = primary ? gold : surface2,
                Foreground = primary ? bg : text,
                BorderThickness = primary ? new global::Avalonia.Thickness(0)
                    : new global::Avalonia.Thickness(1),
                BorderBrush = border
            };
            button.Click += (_, _) =>
            {
                dialog._result = index;
                dialog.Close();
            };
            dialog.ButtonPanel.Children.Add(button);
        }

        await dialog.ShowDialog(owner);
        return dialog._result;
    }
}
