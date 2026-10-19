using 烧录上位机程序.ViewModels;

namespace 烧录上位机程序.Pages;

public partial class IpScanPage : BasePage
{
    public IpScanViewModel ViewModel { get; } = new();

    public IpScanPage()
    {
        InitializeComponent();
        DataContext = ViewModel;
    }
}
