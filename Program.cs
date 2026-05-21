namespace AudioPttLatch;

/// <summary>
/// Application entry point.
/// </summary>
static class Program
{
    /// <summary>
    /// Initializes WinForms and opens the main configuration window.
    /// </summary>
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new Form1());
    }    
}
