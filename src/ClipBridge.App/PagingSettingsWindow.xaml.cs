using System.Globalization;
using System.Windows;

namespace ClipBridge.App;

public partial class PagingSettingsWindow : Window
{
    public int PageSize { get; private set; }
    public bool LoopPages { get; private set; }
    public PagingSettingsWindow(int pageSize, bool loopPages)
    {
        InitializeComponent();
        PageSizeInput.Text = Math.Clamp(pageSize, 1, 36).ToString(CultureInfo.InvariantCulture);
        LoopInput.IsChecked = loopPages;
        Loaded += (_, _) => { PageSizeInput.Focus(); PageSizeInput.SelectAll(); };
    }
    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PageSizeInput.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value is < 1 or > 36)
        { ErrorText.Text = "表示件数は1〜36の整数で入力してください。"; PageSizeInput.Focus(); return; }
        PageSize = value;
        LoopPages = LoopInput.IsChecked == true;
        DialogResult = true;
    }
}
