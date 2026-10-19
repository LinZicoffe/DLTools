using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;

namespace 烧录上位机程序.Services;

public static class I18nService
{
    private static readonly List<string> SupportedLangs = new() { "zh_CN", "zh_TW", "ja", "ko", "en", "fr" };

    private static Dictionary<string, string>? _translations;
    private static string _currentLang = "zh_CN";
    private static readonly object _lock = new();

    public static string CurrentLang => _currentLang;
    public static IReadOnlyList<string> SupportedLanguages => SupportedLangs;

    /// <summary>语言切换完成（含启动首次加载）后触发；UI 借此刷新已绑定的本地化文本。</summary>
    public static event Action? LanguageChanged;

    public static string GetString(string key)
    {
        lock (_lock)
        {
            if (_translations != null && _translations.TryGetValue(key, out var val))
                return val;
            return key;
        }
    }

    public static void LoadLanguage(string lang)
    {
        if (!SupportedLangs.Contains(lang)) lang = "zh_CN";
        lock (_lock)
        {
            _currentLang = lang;
            _translations = null;
            try
            {
                var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "i18n");
                var path = Path.Combine(dir, $"{lang}.json");
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    _translations = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                }
            }
            catch { }
            _translations ??= new Dictionary<string, string>();
        }
        LanguageChanged?.Invoke();
    }
}
