using System.IO;
using System.Windows;

namespace BmtExporter;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args is ["--qa", var input, var output])
        {
            Shutdown(Qa.Run(input, output));
            return;
        }
        var window = new MainWindow();
        window.Show();
    }
}
