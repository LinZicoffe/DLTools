using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace 烧录上位机程序.Services;

public sealed class ConfigService
{
    private static readonly ConfigService _instance = new();
    public static ConfigService Instance => _instance;

    private readonly string _prefsPath;
    private readonly object _lock = new();
    private Timer? _flushTimer;
    private bool _dirty = false;

    private Dictionary<string, object> _config = new();

    private ConfigService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _prefsPath = Path.Combine(appData, "JLinkRTTViewer", "user_prefs.json");
        _config = LoadDefaults();
        Load();
    }

    public static void Initialize() { }
    public static T Get<T>(string key) => _instance.GetInternal<T>(key);
    public static void Set(string key, object value) => _instance.SetInternal(key, value);
    public static void Flush() => _instance.FlushInternal();

    private Dictionary<string, object> LoadDefaults() => new()
    {
        ["target_mcu"] = "",
        ["interface"] = "SWD",
        ["speed_khz"] = 4000,
        ["rtt_channel"] = 0,
        ["theme"] = "auto",
        ["theme_color"] = "#28afe9",
        ["language"] = "zh_CN",
        ["font_family"] = "Consolas",
        ["font_size"] = 13,
        ["memory_font_size"] = 12,
        ["ui_font_size"] = 9,
        ["ui_font_family"] = "",
        ["dpi_scale"] = "Auto",
        ["max_display_lines"] = 10000,
        ["rtt_poll_interval_ms"] = 100,
        ["rtt_encoding"] = "utf-8",
        ["auto_scroll"] = true,
        ["power_output"] = false,
        ["mark_color"] = "#ffff55",
        ["auto_reconnect"] = false,
        ["reset_mode"] = "normal",
        ["window_geometry"] = "",
    };

    private T GetInternal<T>(string key)
    {
        lock (_lock)
        {
            if (_config.TryGetValue(key, out var val) && val is T t)
                return t;
            return default!;
        }
    }

    private void SetInternal(string key, object value)
    {
        lock (_lock)
        {
            _config[key] = value;
            _dirty = true;
        }
        ScheduleFlush();
    }

    private void FlushInternal()
    {
        lock (_lock)
        {
            if (!_dirty) return;
            // 只有落盘成功才清脏标记，失败时保留以便下次重试
            if (Save()) _dirty = false;
        }
    }

    private void ScheduleFlush()
    {
        _flushTimer ??= new Timer(_ => FlushInternal(), null, 200, Timeout.Infinite);
        _flushTimer.Change(200, Timeout.Infinite);
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_prefsPath))
            {
                var json = File.ReadAllText(_prefsPath);
                // 不能反序列化为 Dictionary<string, object>：System.Text.Json 会把值装箱成
                // JsonElement，导致 Get<T> 里的 "val is T" 永远匹配失败、配置全部读不回来
                var loaded = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                if (loaded != null)
                {
                    lock (_lock)
                    {
                        foreach (var kvp in loaded)
                            _config[kvp.Key] = ConvertElement(kvp.Value);
                    }
                }
            }
        }
        catch { }
    }

    private static object ConvertElement(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                return el.GetString() ?? "";
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number:
                if (el.TryGetInt32(out int i)) return i;
                if (el.TryGetInt64(out long l)) return l;
                if (el.TryGetDouble(out double d)) return d;
                return el.GetRawText();
            default:
                return el.GetRawText();
        }
    }

    private bool Save()
    {
        var tmpPath = _prefsPath + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(_prefsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tmpPath, json);
            // File.Replace 要求目标已存在：全新安装下 user_prefs.json 尚未创建时会抛
            // FileNotFoundException，导致配置永远无法首次落盘
            if (File.Exists(_prefsPath))
                File.Replace(tmpPath, _prefsPath, null);
            else
                File.Move(tmpPath, _prefsPath);
            return true;
        }
        catch (Exception ex)
        {
            try { LoggerService.Error($"保存配置失败: {ex.Message}"); } catch { }
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            return false;
        }
    }
}
