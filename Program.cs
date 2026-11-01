namespace QarttzVPN;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Один экземпляр.
        using var mutex = new Mutex(true, "QarttzVPN_SingleInstance", out bool created);
        if (!created) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
