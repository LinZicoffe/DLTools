using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 烧录上位机程序.Pages;
using 烧录上位机程序.Services;

namespace 烧录上位机程序.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly ConfigService _config;

    public MainWindowViewModel()
    {
        _config = ConfigService.Instance;
        Pages = new ObservableCollection<BasePage>
        {
            new RttMonitorPage(),
            new MemoryViewerPage(),
            new FlashPage(),
            new PackManagerPage(),
            new SettingsPage(),
            new AboutPage()
        };
        CurrentPage = Pages[0];
    }

    [ObservableProperty]
    private BasePage _currentPage = new RttMonitorPage();

    public ObservableCollection<BasePage> Pages { get; }

    [RelayCommand]
    private void NavigateTo(int index)
    {
        if (index >= 0 && index < Pages.Count)
            CurrentPage = Pages[index];
    }

    [RelayCommand]
    private void SwitchLanguage(string lang)
    {
        I18nService.LoadLanguage(lang);
        ConfigService.Set("language", lang);
    }
}
