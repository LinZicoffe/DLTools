using System;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;

namespace 烧录上位机程序.Services;

/// <summary>
/// I18n 绑定源：索引器经 Binding 绑定到 XAML 文本，语言切换时触发
/// PropertyChanged("Item[]") 令所有 {i18n:Loc} 绑定即时刷新。
/// </summary>
public sealed class I18nSource : INotifyPropertyChanged
{
    public static I18nSource Instance { get; } = new();

    static I18nSource()
    {
        I18nService.LanguageChanged += () => Instance
            .PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs("Item[]"));
    }

    private I18nSource() { }

    /// <summary>WPF 索引器绑定路径：Binding "[key]" 会命中本成员。</summary>
    public string this[string key] => I18nService.GetString(key);

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// XAML 本地化标记扩展。用法：Text="{i18n:Loc '连接参数'}"。
/// 内部返回绑定到 I18nSource 索引器的 Binding，切换语言时自动更新。
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public class LocExtension : MarkupExtension
{
    public string Key { get; set; }

    public LocExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        // key 里的空格/冒号等字符必须整体加引号作为索引器参数
        var binding = new Binding($"[\"{Key}\"]")
        {
            Source = I18nSource.Instance,
            Mode = BindingMode.OneWay,
        };
        return binding.ProvideValue(serviceProvider);
    }
}
