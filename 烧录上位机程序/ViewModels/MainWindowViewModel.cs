using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 烧录上位机程序.Pages;

namespace 烧录上位机程序.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly FlashPage _flashPage = new();
    private readonly PackagingPage _packagingPage = new();

    public MainWindowViewModel()
    {
        CurrentPage = _flashPage;
    }

    [ObservableProperty]
    private BasePage? _currentPage;

    [RelayCommand]
    private void ShowFlash() => CurrentPage = _flashPage;

    [RelayCommand]
    private void ShowPackaging() => CurrentPage = _packagingPage;
}
