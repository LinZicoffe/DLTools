using System.Windows;
using System.Windows.Controls;
using 烧录上位机程序.ViewModels;

namespace 烧录上位机程序.Pages;

public partial class PackagingPage : BasePage
{
    public PackagingViewModel ViewModel { get; } = new();

    public PackagingPage()
    {
        InitializeComponent();
        DataContext = ViewModel;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        // Cached pages may load again after navigation; initialization itself is idempotent.
        await ViewModel.InitializeAsync();
    }

    private void LogBox_TextChanged(object sender, TextChangedEventArgs e) => LogBox.ScrollToEnd();
}
