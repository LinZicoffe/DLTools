using 烧录上位机程序.ViewModels;

namespace 烧录上位机程序.Pages;

public partial class CleanupPage : BasePage
{
    public CleanupViewModel ViewModel { get; } = new();

    public CleanupPage()
    {
        InitializeComponent();
        DataContext = ViewModel;
    }
}
