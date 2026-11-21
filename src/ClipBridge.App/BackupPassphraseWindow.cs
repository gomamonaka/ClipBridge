using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClipBridge.App;

internal sealed class BackupPassphraseWindow : Window
{
    private readonly PasswordBox input = new() { Margin = new Thickness(0, 12, 0, 12), Padding = new Thickness(8), MinHeight = 34 };
    public string Passphrase => input.Password;
    public BackupPassphraseWindow(string current)
    {
        Title = "バックアップから復元"; Width = 460; Height = 270; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        FontFamily = new FontFamily("Yu Gothic UI"); Background = new SolidColorBrush(Color.FromRgb(245, 248, 252));
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "バックアップ作成時の合言葉", FontSize = 18, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "現在の合言葉を入力済みです。異なる場合は変更してください。復元は既存データへの追加・更新として行います。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        input.Password = current; panel.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "キャンセル", IsCancel = true, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "復元する", IsDefault = true, Padding = new Thickness(12, 5, 12, 5) };
        save.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(Passphrase)) DialogResult = true; };
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons); Content = panel;
    }
}
