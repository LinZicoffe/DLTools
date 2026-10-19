using OfflineAuthorization.Demo.Core;

namespace OfflineAuthorization.WinForms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Recreate the PUBLIC fixture for automated setup; no real generator profiles are read.
        if (args.Length == 2 && args[0] == "--export-demo-profile")
        {
            DemoProfile.ExportAsync(args[1]).GetAwaiter().GetResult();
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
