using System;
using System.IO;
using System.Windows;

namespace 烧录上位机程序.Services;

public static class LoggerService
{
    private static readonly string LogDir;
    private static StreamWriter? _logWriter;

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
            var date = DateTime.Now.ToString("yyyyMMdd");
            var logFile = Path.Combine(LogDir, $"rtt_{date}.log");
            _logWriter ??= new StreamWriter(logFile, true) { AutoFlush = true };
            _logWriter.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{level}] {message}");
        }
        catch { }
    }
}
