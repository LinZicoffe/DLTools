using System.Windows;
using System.Windows.Controls;
using 烧录上位机程序.Services;

namespace 烧录上位机程序;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new 烧录上位机程序.ViewModels.MainWindowViewModel();

        // 设置默认语言
        var vm = (烧录上位机程序.ViewModels.MainWindowViewModel)DataContext!;
        var lang = ConfigService.Get<string>("language") ?? "zh_CN";
        I18nService.LoadLanguage(lang);
        UpdateLanguageCombo(lang);
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox comboBox && comboBox.SelectedItem is ComboBoxItem item)
        {
            var lang = item.Tag?.ToString() ?? "zh_CN";
            I18nService.LoadLanguage(lang);
            ConfigService.Set("language", lang);
        }
    }

    private void UpdateLanguageCombo(string lang)
    {
        if (LanguageCombo is ComboBox combo)
        {
            foreach (ComboBoxItem item in combo.Items)
            {
                if (item.Tag?.ToString() == lang)
                {
                    item.IsSelected = true;
                    break;
                }
            }
        }
    }
}
