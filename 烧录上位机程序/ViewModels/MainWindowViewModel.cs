using CommunityToolkit.Mvvm.ComponentModel;
using 烧录上位机程序.Pages;

namespace 烧录上位机程序.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel()
    {
        // 应用只保留固件烧录功能，其他页面已按需求移除
        CurrentPage = new FlashPage();
    }

    [ObservableProperty]
    private BasePage? _currentPage;
}
