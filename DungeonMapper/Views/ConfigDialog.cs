using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DungeonMapper.Views;

/// Small modal dialog for editing a feature's fields. Built in code (no XAML)
/// to keep it lightweight. Fields: label + TextBox or ComboBox.
/// OK applies the field callbacks; Cancel discards.
public sealed class ConfigDialog : Window
{
    private readonly StackPanel _fields = new();
    private Action[] _apply = Array.Empty<Action>();

    public ConfigDialog(string title)
    {
        Title = title;
        Width = 320;
        SizeToContent = SizeToContent.Manual;
        CanResize = false;
        Background = new SolidColorBrush(Color.FromRgb(30, 30, 38));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel { Margin = new Thickness(12), Spacing = 8 };
        root.Children.Add(new TextBlock { Text = title, FontSize = 16, Foreground = Brushes.White });

        _fields.Spacing = 6;
        root.Children.Add(_fields);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        var ok = new Button { Content = "OK", MinWidth = 70 };
        var cancel = new Button { Content = "Cancel", MinWidth = 70 };
        ok.Click += (_, _) =>
        {
            foreach (var a in _apply) a();
            Close();
        };
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);

        Content = root;
    }

    public void AddText(string label, string initial, Action<string> onApply)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White });
        var tb = new TextBox { Text = initial };
        panel.Children.Add(tb);
        _fields.Children.Add(panel);
        Array.Resize(ref _apply, _apply.Length + 1);
        _apply[^1] = () => onApply(tb.Text);
    }

    public void AddChoice(string label, string[] options, string initial, Action<string> onApply)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White });
        var cb = new ComboBox
        {
            ItemsSource = options,
            SelectedItem = Array.IndexOf(options, initial) >= 0 ? initial : options[0],
        };
        panel.Children.Add(cb);
        _fields.Children.Add(panel);
        Array.Resize(ref _apply, _apply.Length + 1);
        _apply[^1] = () => onApply((string?)cb.SelectedItem ?? options[0]);
    }
}
