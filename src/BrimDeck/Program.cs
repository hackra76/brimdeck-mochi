namespace BrimDeck;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // A newer portable version started to replace this folder's files; it does that and exits without opening the app.
        if (Core.PortableUpdate.TryRun(args, out var exitCode)) { Environment.ExitCode = exitCode; return; }
        // The elevated instance FramePermission starts adds the user to Performance Log Users and exits.
        if (args is [Native.FramePermission.ElevatedSwitch, var user]) { Environment.ExitCode = Native.FramePermission.RunElevated(user); return; }
        // The elevated instance the enhanced sensors page starts sets up or removes the sensor service and exits.
        if (args is [Native.SensorSetup.ElevatedSwitch, .. var sensors]) { Environment.ExitCode = Native.SensorSetup.RunElevated(sensors); return; }
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
