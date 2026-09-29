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
    public static string GetString(string key) => _instance.GetInternal<string>(key);

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

    private string GetInternalString(string key)
    {
        lock (_lock)
        {
            return _config.TryGetValue(key, out var val) ? val?.ToString() ?? "" : "";
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
            _dirty = false;
            Save();
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
                var loaded = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                if (loaded != null)
                {
                    lock (_lock)
                    {
                        foreach (var kvp in loaded)
                            _config[kvp.Key] = kvp.Value;
                    }
                }
            }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_prefsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
            var tmpPath = _prefsPath + ".tmp";
            File.WriteAllText(tmpPath, json);
            File.Replace(tmpPath, _prefsPath, null);
        }
        catch { }
    }
}
