using 烧录上位机程序.ViewModels;

namespace 烧录上位机程序.Pages;

public partial class DownloadPage : BasePage
{
    public DownloadViewModel ViewModel { get; } = new();

    public DownloadPage()
    {
        InitializeComponent();
        DataContext = ViewModel;
    }
}
