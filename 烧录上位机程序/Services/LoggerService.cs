using System;
using System.IO;
using System.Threading;

namespace 烧录上位机程序.Services;

public static class LoggerService
{
    private static readonly string LogDir;
    private static readonly object _lock = new();
    private static StreamWriter? _logWriter;
    private static string _currentDate = "";

    static LoggerService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        LogDir = Path.Combine(appData, "JLinkRTTViewer", "logs");
        Directory.CreateDirectory(LogDir);
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    public static string GetLogDir() => LogDir;

    private static void Write(string level, string message)
    {
        try
        {
            // 全局异常钩子注册后，任意线程都可能写日志：串行化写入
            lock (_lock)
            {
                var date = DateTime.Now.ToString("yyyyMMdd");
                // 跨天时切换到新日志文件（此前 writer 只在首次创建，日期变量算了不用，
                // 跨零点后所有日志仍写进前一天文件）
                if (_logWriter == null || date != _currentDate)
                {
                    _logWriter?.Dispose();
                    var logFile = Path.Combine(LogDir, $"rtt_{date}.log");
                    // FileShare.ReadWrite：允许外部工具在程序运行期间读取日志
                    // （StreamWriter(path) 默认 FileShare.Read 会锁住文件）
                    var stream = new FileStream(logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    _logWriter = new StreamWriter(stream) { AutoFlush = true };
                    _currentDate = date;
                }
                _logWriter.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{level}] {message}");
            }
        }
        catch { }
    }
}
