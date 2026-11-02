using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using wetwiLAG.Models;
using wetwiLAG.Services;

namespace wetwiLAG.Infrastructure;

public static class InputDialog
{
    public static string? Show(string title, string label, string initialValue, Window? owner)
    {
        var dialog = new Window
        {
            Title = title,
            Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 340,
            Background = TryResourceBrush("PanelBackground") ?? Brushes.White,
            Foreground = TryResourceBrush("PrimaryText") ?? Brushes.Black,
            ShowInTaskbar = false
        };
        var input = new TextBox
        {
            Text = initialValue,
            MinWidth = 280,
            Margin = new Thickness(0, 8, 0, 16),
            Padding = new Thickness(9, 7, 9, 7)
        };
        var save = new Button
        {
            Content = "Save",
            IsDefault = true,
            MinWidth = 90,
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 8, 0)
        };
        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 90,
            Padding = new Thickness(14, 7, 14, 7)
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        var content = new StackPanel { Margin = new Thickness(20) };
        content.Children.Add(new TextBlock { Text = label });
        content.Children.Add(input);
        content.Children.Add(buttons);
        dialog.Content = content;
        save.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }

    private static Brush? TryResourceBrush(string key) =>
        Application.Current?.TryFindResource(key) as Brush;
}

public static class ProfileDialog
{
    public static UserProfile? ShowEditor(
        Window? owner,
        UserProfile? profile,
        IReadOnlyList<NetworkAdapterInfo> adapters,
        string? selectedAdapterId)
    {
        var dialog = new Window
        {
            Title = profile is null ? "Create profile" : "Edit profile",
            Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 440,
            Background = Application.Current?.TryFindResource("PanelBackground") as Brush ?? Brushes.White,
            Foreground = Application.Current?.TryFindResource("PrimaryText") as Brush ?? Brushes.Black,
            ShowInTaskbar = false
        };
        var nameInput = new TextBox
        {
            Text = profile?.Name ?? "",
            Margin = new Thickness(0, 6, 0, 14),
            Padding = new Thickness(9, 7, 9, 7)
        };
        var probeInput = new TextBox
        {
            Text = profile?.ProbeHost ?? "1.1.1.1",
            Margin = new Thickness(0, 6, 0, 14),
            Padding = new Thickness(9, 7, 9, 7)
        };
        var choices = new List<AdapterChoice> { new(null, "Use default adapter") };
        choices.AddRange(adapters.Select(adapter => new AdapterChoice(adapter.Id, $"{adapter.Name} · {adapter.Status}")));
        var adapterInput = new ComboBox
        {
            ItemsSource = choices,
            DisplayMemberPath = nameof(AdapterChoice.Display),
            SelectedValuePath = nameof(AdapterChoice.Id),
            SelectedItem = choices.FirstOrDefault(choice =>
                               choice.Id == (profile?.PreferredAdapterId ?? selectedAdapterId))
                           ?? choices[0],
            Margin = new Thickness(0, 6, 0, 14),
            Padding = new Thickness(7)
        };
        var editor = new Grid();
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddEditorRow(editor, 0, "Profile name", nameInput);
        AddEditorRow(editor, 1, "Probe target", probeInput);
        AddEditorRow(editor, 2, "Preferred adapter", adapterInput);
        var note = new TextBlock
        {
            Text = "Profiles store app preferences only. Selecting a profile never changes Windows network settings.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Application.Current?.TryFindResource("MutedText") as Brush
        };
        Grid.SetRow(note, 3);
        Grid.SetColumn(note, 0);
        Grid.SetColumnSpan(note, 2);
        editor.Children.Add(note);

        var save = new Button
        {
            Content = "Save profile",
            IsDefault = true,
            MinWidth = 110,
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 14, 8, 0)
        };
        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 90,
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 14, 0, 0)
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        var content = new StackPanel { Margin = new Thickness(22) };
        content.Children.Add(editor);
        content.Children.Add(buttons);
        dialog.Content = content;

        UserProfile? result = null;
        save.Click += (_, _) =>
        {
            var name = nameInput.Text.Trim();
            var host = probeInput.Text.Trim();
            if (name.Length is < 1 or > 40)
            {
                MessageBox.Show(dialog, "Enter a profile name with 1 to 40 characters.", "Invalid profile", MessageBoxButton.OK, MessageBoxImage.Warning);
                nameInput.Focus();
                return;
            }

            if (!NetworkDiagnostics.IsValidHost(host))
            {
                MessageBox.Show(dialog, "Enter a valid IP address or DNS host name.", "Invalid probe target", MessageBoxButton.OK, MessageBoxImage.Warning);
                probeInput.Focus();
                return;
            }

            var selected = adapterInput.SelectedItem as AdapterChoice;
            result = new UserProfile
            {
                Id = profile?.Id ?? Guid.NewGuid(),
                Name = name,
                ProbeHost = host,
                PreferredAdapterId = selected?.Id,
                PreferredAdapterName = selected?.Display
            };
            dialog.DialogResult = true;
        };
        dialog.Loaded += (_, _) => nameInput.Focus();
        return dialog.ShowDialog() == true ? result : null;
    }

    private static void AddEditorRow(Grid grid, int row, string label, UIElement input)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 0);
        Grid.SetRow(input, row);
        Grid.SetColumn(input, 1);
        grid.Children.Add(text);
        grid.Children.Add(input);
    }

    private sealed record AdapterChoice(string? Id, string Display);
}
