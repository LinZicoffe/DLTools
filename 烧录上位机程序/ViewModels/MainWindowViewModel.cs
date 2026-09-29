using System;
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
        // 应用只保留固件烧录功能，其他页面已按需求移除
        CurrentPage = new FlashPage();
    }

    [ObservableProperty]
    private BasePage? _currentPage;

    [RelayCommand]
    private void SwitchLanguage(string lang)
    {
        I18nService.LoadLanguage(lang);
        ConfigService.Set("language", lang);
    }
}
