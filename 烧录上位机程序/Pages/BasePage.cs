using System.Windows.Controls;

namespace 烧录上位机程序.Pages;

/// <summary>
/// 页面基类。所有页面 XAML 使用 local:BasePage 作为根元素。
/// </summary>
public class BasePage : Page
{
    public new string? Title { get; set; }

    public virtual void Initialize() { }
    public virtual void Shutdown() { }
}
