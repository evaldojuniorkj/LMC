using System.IO;
using System.Windows;

namespace Lmc.Setup;
public partial class App : Application
{
    private Mutex? installationMutex;
    private bool ownsMutex;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Console.OutputEncoding=new System.Text.UTF8Encoding(false);
        if(e.Args.Contains("--self-test"))
        {
            try{await SetupChecks.Run();Shutdown(0);}catch(Exception ex){Console.Error.WriteLine(ex.Message);Shutdown(1);}return;
        }
        var packageCheck=Array.IndexOf(e.Args,"--check-package");
        if(packageCheck>=0)
        {
            try{await SetupChecks.Package(e.Args.Length>packageCheck+1?e.Args[packageCheck+1]:null);Shutdown(0);}catch(Exception ex){Console.Error.WriteLine(ex.Message);Shutdown(1);}return;
        }
        var render=Array.IndexOf(e.Args,"--render-preview");
        var preview=render>=0;
        if(!preview&&!InstallerEngine.IsAdministrator()){MessageBox.Show("Abra Instalar-LMC.exe como Administrador para configurar o serviço.","Instalador LMC");Shutdown(1);return;}
        if(!preview)
        {
            installationMutex=new Mutex(false,@"Global\LMC-VisualSetup");
            try{ownsMutex=installationMutex.WaitOne(0);}catch(AbandonedMutexException){ownsMutex=true;}
            if(!ownsMutex){MessageBox.Show("Já existe um instalador LMC aberto neste computador. Use essa janela para continuar.","Instalador LMC");Shutdown(1);return;}
        }
        var window=new MainWindow(preview);MainWindow=window;
        if(preview)
        {
            window.Left=-20000;window.Top=-20000;window.WindowStartupLocation=WindowStartupLocation.Manual;window.ShowInTaskbar=false;
            window.Show();await window.RenderPreviews(Path.GetFullPath(e.Args[render+1]));window.Close();return;
        }
        window.Show();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if(ownsMutex)installationMutex?.ReleaseMutex();installationMutex?.Dispose();base.OnExit(e);
    }
}
