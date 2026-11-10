using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using 烧录上位机程序.Models.Cleanup;

namespace 烧录上位机程序.Services.Cleanup;

/// <summary>清理设置持久化：%APPDATA%\JLinkRTTViewer\cleaner\settings.json。
/// 跨进程命名 Mutex + 原子替换写；损坏时抛异常不静默重建；数值加载时钳制到合法区间（用户偏好，非身份关键）。</summary>
public sealed class CleanupSettingsStore
{
    private const int MaxAgeDays = 3650;
    private const int MaxKeepCount = 20;

    public string FilePath { get; }

    public CleanupSettingsStore(string? filePath = null)
    {
        FilePath = Path.GetFullPath(filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JLinkRTTViewer", "cleaner", "settings.json"));
    }

    public CleanupSettings Load()
    {
        if (!File.Exists(FilePath)) return CleanupSettings.Default;
        SettingsDocument document;
        try
        {
            document = JsonSerializer.Deserialize<SettingsDocument>(File.ReadAllText(FilePath))
                ?? throw new JsonException("空文档");
            if (document.SchemaVersion != 1) throw new JsonException($"不支持的设置版本 {document.SchemaVersion}");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new IOException("清理设置无法读取（" + ex.Message + "），请保留并修复该文件（或删除后重开程序恢复默认）："
                + FilePath, ex);
        }
        return Clamp(ToSettings(document));
    }

    public void Save(CleanupSettings settings)
    {
        var document = ToDocument(Clamp(settings));
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
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
            else File.Move(temporary, FilePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static CleanupSettings Clamp(CleanupSettings settings) => new()
    {
        FreshnessHours = Math.Clamp(settings.FreshnessHours, 1, 24 * 30),
        LogAgeDays = Math.Clamp(settings.LogAgeDays, 1, MaxAgeDays),
        ArtifactAgeDays = Math.Clamp(settings.ArtifactAgeDays, 1, MaxAgeDays),
        ConversationAgeDays = Math.Clamp(settings.ConversationAgeDays, 1, MaxAgeDays),
        BackupKeepCount = Math.Clamp(settings.BackupKeepCount, 1, MaxKeepCount)
    };

    private static SettingsDocument ToDocument(CleanupSettings settings) => new()
    {
        FreshnessHours = settings.FreshnessHours,
        LogAgeDays = settings.LogAgeDays,
        ArtifactAgeDays = settings.ArtifactAgeDays,
        ConversationAgeDays = settings.ConversationAgeDays,
        BackupKeepCount = settings.BackupKeepCount
    };

    private static CleanupSettings ToSettings(SettingsDocument document) => new()
    {
        FreshnessHours = document.FreshnessHours,
        LogAgeDays = document.LogAgeDays,
        ArtifactAgeDays = document.ArtifactAgeDays,
        ConversationAgeDays = document.ConversationAgeDays,
        BackupKeepCount = document.BackupKeepCount
    };

    private Mutex AcquireSaveLock()
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FilePath.ToUpperInvariant())));
        var mutex = new Mutex(false, @"Local\DLToolsCleanupSettings_" + key);
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
        {
            mutex.Dispose();
            throw new IOException("其他工具实例正在保存清理设置，请稍后重试。");
        }
        return mutex;
    }

    public sealed class SettingsDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public int FreshnessHours { get; set; } = 24;
        public int LogAgeDays { get; set; } = 7;
        public int ArtifactAgeDays { get; set; } = 30;
        public int ConversationAgeDays { get; set; } = 90;
        public int BackupKeepCount { get; set; } = 3;
    }
}
