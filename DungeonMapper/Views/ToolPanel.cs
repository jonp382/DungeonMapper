using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DungeonMapper.Views;

/// Right-docked floating panel that hosts per-tool options (erase filter,
/// paste ghost toggle) and the config fields for a selected door/stair/label.
/// Replaces the modal ConfigDialog. Fields apply live on change.
public sealed class ToolPanel : Border
{
    private readonly TextBlock _title = new() { FontSize = 14, Foreground = Brushes.White };
    private readonly StackPanel _content = new() { Spacing = 6 };

    public ToolPanel()
    {
        Background = new SolidColorBrush(Color.FromRgb(30, 30, 38));
        Padding = new Thickness(10);
        Width = 220;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(0, 48, 8, 0); // sits below the toolbar, over the canvas
        var root = new StackPanel { Spacing = 8 };
        root.Children.Add(_title);
        root.Children.Add(_content);
        Child = root;
    }

    public void Clear(string title)
    {
        _title.Text = title;
        _content.Children.Clear();
    }

    public void AddHint(string text)
    {
        _content.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
        });
    }

    public void AddCheck(string label, bool initial, Action<bool> onChange)
    {
        var cb = new CheckBox { Content = label, IsChecked = initial, Foreground = Brushes.White };
        cb.IsCheckedChanged += (_, _) => onChange(cb.IsChecked == true);
        _content.Children.Add(cb);
    }

    public void AddText(string label, string initial, Action<string> onChange)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White });
        var tb = new TextBox { Text = initial };
        tb.TextChanged += (_, _) => onChange(tb.Text);
        panel.Children.Add(tb);
        _content.Children.Add(panel);
    }

    public void AddChoice(string label, string[] options, string initial, Action<string> onChange)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White });
        var cb = new ComboBox
        {
            ItemsSource = options,
            SelectedItem = Array.IndexOf(options, initial) >= 0 ? initial : options[0],
        };
        cb.SelectionChanged += (_, _) => onChange((string?)cb.SelectedItem ?? options[0]);
        panel.Children.Add(cb);
        _content.Children.Add(panel);
    }
}
