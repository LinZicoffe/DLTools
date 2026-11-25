using 烧录上位机程序.ViewModels;

namespace 烧录上位机程序.Pages;

public partial class IpPurityPage : BasePage
{
    public IpPurityViewModel ViewModel { get; }

    public IpPurityPage()
        : this(new IpPurityViewModel())
    {
    }

    public IpPurityPage(IpPurityViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = ViewModel;
    }
}
