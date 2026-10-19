using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Reflection;

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
    case "flood":
        var count = int.Parse(args[1]);
        await Task.WhenAll(Task.Run(() =>
        {
            for (var i = 0; i < count; i++) Console.WriteLine("out:" + i + ":" + new string('界', 256));
        }), Task.Run(() =>
        {
            for (var i = 0; i < count; i++) Console.Error.WriteLine("err:" + i + ":" + new string('文', 256));
        }));
        return 0;
    case "recovery-register":
        var assembly = Assembly.LoadFrom(args[1]);
        var registryType = assembly.GetType("烧录上位机程序.Services.Packaging.RecoveryRegistry", throwOnError: true)!;
        var registry = Activator.CreateInstance(registryType, new object?[] { args[2] })!;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
        {
            File.WriteAllText(args[8], "ready");
            while (!File.Exists(args[6])) await Task.Delay(10, timeout.Token);
            string journal;
            try
            {
                journal = (string)registryType.GetMethod("Register", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(registry, new object?[] { args[3], args[4], args[5] })!;
            }
            catch (TargetInvocationException error) when (error.InnerException is IOException)
            {
                Console.WriteLine("blocked");
                return 17;
            }
            Console.WriteLine("registered");
            try
            {
                while (!File.Exists(args[7])) await Task.Delay(10, timeout.Token);
            }
            finally
            {
                registryType.GetMethod("Complete", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(registry, new object[] { journal });
            }
        }
        return 0;
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
