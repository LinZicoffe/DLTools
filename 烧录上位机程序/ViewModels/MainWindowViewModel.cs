using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 烧录上位机程序.Pages;

namespace 烧录上位机程序.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly FlashPage _flashPage = new();
    private readonly PackagingPage _packagingPage = new();
    private readonly OfflineAuthorizationPage _authorizationPage = new();
    private readonly IpScanPage _ipScanPage = new();
    private readonly DownloadPage _downloadPage = new();

    public MainWindowViewModel() => CurrentPage = _flashPage;

    [ObservableProperty] private BasePage? _currentPage;
    public bool IsFlashSelected => ReferenceEquals(CurrentPage, _flashPage);
    public bool IsPackagingSelected => ReferenceEquals(CurrentPage, _packagingPage);
    public bool IsAuthorizationSelected => ReferenceEquals(CurrentPage, _authorizationPage);
    public bool IsIpScanSelected => ReferenceEquals(CurrentPage, _ipScanPage);
    public bool IsDownloadSelected => ReferenceEquals(CurrentPage, _downloadPage);

    partial void OnCurrentPageChanged(BasePage? value)
    {
        OnPropertyChanged(nameof(IsFlashSelected));
        OnPropertyChanged(nameof(IsPackagingSelected));
        OnPropertyChanged(nameof(IsAuthorizationSelected));
        OnPropertyChanged(nameof(IsIpScanSelected));
        OnPropertyChanged(nameof(IsDownloadSelected));
    }

    [RelayCommand] private void ShowFlash() => CurrentPage = _flashPage;
    [RelayCommand] private void ShowPackaging() => CurrentPage = _packagingPage;
    [RelayCommand] private void ShowAuthorization() => CurrentPage = _authorizationPage;
    [RelayCommand] private void ShowIpScan() => CurrentPage = _ipScanPage;
    [RelayCommand] private void ShowDownload() => CurrentPage = _downloadPage;
}
