using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

static class Program
{
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("Crash: " + e.ExceptionObject);
        if (args.Length > 0 && args[0] == "--agent")
        {
            // Core Audio callbacks arrive on worker threads; keep the agent off the STA.
            var thread = new Thread(Agent.Run);
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            thread.Join();
            return;
        }

        bool created;
        using (new Mutex(true, @"Local\SonarFollow.UI", out created))
        {
            if (!created) return;
            if (!Agent.IsRunning()) Agent.Start();

            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SettingsForm());
        }
    }
}
