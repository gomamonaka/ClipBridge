using System.Windows;

namespace ClipBridge.App;

public partial class App
{
    private void ConfigurePaging()
    {
        if (window is null || configuring || exiting) return;
        bool previous = window.SuppressAutoHide; window.SuppressAutoHide = true;
        try
        {
            var dialog = new PagingSettingsWindow(settings.PopupPageSize, settings.LoopPages);
            if (window.IsVisible) dialog.Owner = window;
            if (dialog.ShowDialog() != true) return;
            var next = settings with { PopupPageSize = dialog.PageSize, LoopPages = dialog.LoopPages };
            next.Save();
            settings = next;
            window.ConfigurePaging(settings.PopupPageSize, settings.LoopPages);
            interaction?.PlacePopup(window, window.FitToWorkArea);
            window.ShowNotice("ページ切り替えの設定を保存しました。");
        }
        catch (Exception ex) { ShowError("ページ切り替えの設定を保存できませんでした。", ex); }
        finally { window.SuppressAutoHide = previous; }
    }
}
