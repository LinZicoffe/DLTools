using System.Diagnostics;
using System.Text;
using System.Text.Json;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;
switch (args.FirstOrDefault())
{
    case "echo":
        Console.WriteLine(JsonSerializer.Serialize(args.Skip(1).ToArray()));
        Console.Error.WriteLine("stderr 中文");
        return 0;
    case "fail":
        Console.Error.WriteLine("expected failure");
        return 17;
    case "wait":
        using (var file = new FileStream(args[1], FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
        {
            Console.WriteLine("ready:" + Environment.ProcessId);
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        return 0;
    case "spawn-and-exit":
        Console.WriteLine("parent:" + Environment.ProcessId);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("wait");
        start.ArgumentList.Add(args[1]);
        using (var child = Process.Start(start)!)
            Console.WriteLine("child:" + child.Id);
        return 0;
    default:
        Console.Error.WriteLine("Unknown test operation");
        return 2;
}
