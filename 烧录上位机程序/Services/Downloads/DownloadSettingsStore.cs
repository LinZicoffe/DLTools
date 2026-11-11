using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using 烧录上位机程序.Models.Downloads;

namespace 烧录上位机程序.Services.Downloads;

/// <summary>下载设置持久化：%APPDATA%\JLinkRTTViewer\downloads\settings.json。
/// 独立于烧录偏好（user_prefs.json）与打包档案；跨进程命名 Mutex + 原子替换写；
/// 损坏时抛异常不静默重建。数值越界在加载时钳制到合法区间（用户偏好，非身份关键）。</summary>
public sealed class DownloadSettingsStore
{
    private const int MaxConnections = SegmentPlanner.MaxConnections;
    private const int MaxConcurrentTasks = 8;

    public string FilePath { get; }

    public DownloadSettingsStore(string? filePath = null)
    {
        FilePath = Path.GetFullPath(filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JLinkRTTViewer", "downloads", "settings.json"));
    }

    public DownloadSettings Load()
    {
        if (!File.Exists(FilePath)) return new DownloadSettings();
        SettingsDocument document;
        try
        {
            document = JsonSerializer.Deserialize<SettingsDocument>(File.ReadAllText(FilePath))
                ?? throw new JsonException("空文档");
            if (document.SchemaVersion != 1) throw new JsonException($"不支持的设置版本 {document.SchemaVersion}");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new IOException("下载设置无法读取（" + ex.Message + "），请保留并修复该文件（或删除后重开程序恢复默认）："
                + FilePath, ex);
        }
        return new DownloadSettings
        {
            DefaultConnections = Math.Clamp(document.DefaultConnections, SegmentPlanner.MinConnections, MaxConnections),
            MaxConcurrentTasks = Math.Clamp(document.MaxConcurrentTasks, 1, MaxConcurrentTasks),
            MaxRateKB = Math.Max(0, document.MaxRateKB),
            ProxyHost = (document.ProxyHost ?? "").Trim(),
            ProxyPort = Math.Clamp(document.ProxyPort, 0, 65535),
            LastDestinationDirectory = document.LastDestinationDirectory ?? ""
        };
    }

    public void Save(DownloadSettings settings)
    {
        var document = new SettingsDocument
        {
            DefaultConnections = Math.Clamp(settings.DefaultConnections, SegmentPlanner.MinConnections, MaxConnections),
            MaxConcurrentTasks = Math.Clamp(settings.MaxConcurrentTasks, 1, MaxConcurrentTasks),
            MaxRateKB = Math.Max(0, settings.MaxRateKB),
            ProxyHost = (settings.ProxyHost ?? "").Trim(),
            ProxyPort = Math.Clamp(settings.ProxyPort, 0, 65535),
            LastDestinationDirectory = settings.LastDestinationDirectory ?? ""
        };
        using var mutex = AcquireSaveLock();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions { WriteIndented = true });
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            AtomicFileWriter.Commit(temporary, FilePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private Mutex AcquireSaveLock()
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FilePath.ToUpperInvariant())));
        var mutex = new Mutex(false, @"Local\DLToolsDownloadSettings_" + key);
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
        {
            mutex.Dispose();
            throw new IOException("其他工具实例正在保存下载设置，请稍后重试。");
        }
        return mutex;
    }

    public sealed class SettingsDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public int DefaultConnections { get; set; } = 8;
        public int MaxConcurrentTasks { get; set; } = 3;
        public long MaxRateKB { get; set; }
        public string? ProxyHost { get; set; }
        public int ProxyPort { get; set; }
        public string? LastDestinationDirectory { get; set; }
    }
}
