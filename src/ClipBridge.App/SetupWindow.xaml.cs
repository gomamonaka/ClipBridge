using System;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace ClipBridge.App;

public partial class SetupWindow : Window
{
    public string SharedFolder { get; private set; } = string.Empty;
    public string Passphrase { get; private set; } = string.Empty;
    public string DeviceName { get; private set; } = string.Empty;

    public SetupWindow(string? sharedFolder = null, string? deviceName = null)
    {
        InitializeComponent();
        FolderInput.Text = sharedFolder ?? DefaultFolder();
        DeviceNameInput.Text = deviceName ?? Environment.MachineName;
    }

    private static string DefaultFolder()
    {
        var root = Environment.GetEnvironmentVariable("OneDriveCommercial")
            ?? Environment.GetEnvironmentVariable("OneDriveConsumer")
            ?? Environment.GetEnvironmentVariable("OneDrive");
        return string.IsNullOrWhiteSpace(root) ? string.Empty : Path.Combine(root, "ClipBridge");
    }

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog
        {
            Title = "OneDrive 内の ClipBridge 共有フォルダーを選択",
            Multiselect = false
        };
        if (Directory.Exists(FolderInput.Text.Trim())) picker.InitialDirectory = FolderInput.Text.Trim();
        if (picker.ShowDialog(this) == true) FolderInput.Text = picker.FolderName;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        string folder = Environment.ExpandEnvironmentVariables(FolderInput.Text.Trim());
        string passphrase = PassphraseInput.Password;
        string device = DeviceNameInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
        { Error("OneDrive 内にある共有フォルダーを、絶対パスで指定してください。", FolderInput); return; }
        try { folder = Path.GetFullPath(folder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { Error("フォルダーのパスを確認してください。", FolderInput); return; }
        if (passphrase.Length < 12)
        { Error("共通の合言葉は 12 文字以上で入力してください。", PassphraseInput); return; }
        if (passphrase != ConfirmPassphraseInput.Password)
        { Error("2 つの合言葉が一致しません。もう一度入力してください。", ConfirmPassphraseInput); return; }
        if (string.IsNullOrWhiteSpace(device))
        { Error("この端末の名前を入力してください。", DeviceNameInput); return; }
        SharedFolder = folder;
        Passphrase = passphrase;
        DeviceName = device;
        DialogResult = true;
    }

    private void Error(string message, UIElement target)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        target.Focus();
    }
}
