using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Lmc.Setup;
public partial class MainWindow : Window
{
    private readonly InstallerEngine engine=new();
    private readonly bool preview;
    private int page;
    private bool busy;
    private string? checkedSql;
    private InstalledInfo? installed;
    private SetupOptions? installing;
    private SetupDefaults? defaults;
    private string? createdOperatorPassword;
    public MainWindow(bool preview=false){this.preview=preview;InitializeComponent();if(!preview){MaxHeight=SystemParameters.WorkArea.Height;Height=Math.Min(790,MaxHeight);}}
    private async void WindowLoaded(object sender,RoutedEventArgs e)
    {
        if(preview)return;
        try
        {
            if(InstallerEngine.ServiceExists()){installed=InstallerEngine.ReadInstalled();MaintenanceUrl.Text=installed.Url;ShowPage(5);await Diagnose();}
            else{defaults=SetupDefaults.Discover();ApplyDefaults(true,true);ShowPage(0);}
        }
        catch(Exception ex){StatusText.Text=ex.Message;NextButton.IsEnabled=false;}
    }
    private void ApplyDefaults(bool sql,bool access)
    {
        if(defaults is null)return;
        var values=defaults.Options;
        if(sql)
        {
            SqlServerBox.ItemsSource=defaults.SqlServers;SqlServerBox.Text=values.SqlServer;SqlUserBox.Text=values.SqlUser;DatabaseBox.Text=values.Database;
            SqlPasswordBox.Password=values.SqlPassword;TrustSqlBox.IsChecked=values.TrustSqlCertificate;checkedSql=null;SqlResult.Text="";
        }
        if(access)
        {
            AddressBox.ItemsSource=SetupOptions.LocalAddresses();AddressBox.Text=values.ServerAddress;PortBox.Text=values.Port.ToString();
            OperatorBox.Text=values.Operator;OperatorPasswordBox.Password=values.OperatorPassword;OperatorConfirmBox.Password=values.OperatorPasswordConfirm;
            TrustHttpsBox.IsChecked=values.TrustLocalHttpsCertificate;ShortcutBox.IsChecked=values.CreateDesktopShortcut;
        }
    }
    private void RestoreDefaultsClicked(object sender,RoutedEventArgs e)
    {
        if(busy)return;ApplyDefaults(page==0,page==1);StatusText.Text="Padrões restaurados. Você pode ajustar os campos antes de continuar.";
    }
    private void CopyOperatorPasswordClicked(object sender,RoutedEventArgs e)
    {
        if(OperatorPasswordBox.Password.Length==0){StatusText.Text="Informe ou gere a senha da interface primeiro.";return;}
        Clipboard.SetText(OperatorPasswordBox.Password);StatusText.Text="Senha da interface copiada.";
    }
    private void GeneratePasswordClicked(object sender,RoutedEventArgs e)
    {
        var value=SetupDefaults.NewOperatorPassword();OperatorPasswordBox.Password=value;OperatorConfirmBox.Password=value;
        StatusText.Text="Nova senha gerada. Copie-a ou edite os campos se preferir.";
    }
    private void CopyCredentialsClicked(object sender,RoutedEventArgs e)
    {
        if(installed is null||createdOperatorPassword is null)return;
        Clipboard.SetText($"Endereço: {installed.Url}\nUsuário: {installed.Operator}\nSenha: {createdOperatorPassword}");
        StatusText.Text="Endereço, login e senha copiados.";
    }
    private SetupOptions Options()=>new(SqlServerBox.Text.Trim(),SqlUserBox.Text.Trim(),SqlPasswordBox.Password,TrustSqlBox.IsChecked==true,
        AddressBox.Text.Trim(),OperatorBox.Text.Trim(),OperatorPasswordBox.Password,OperatorConfirmBox.Password,int.TryParse(PortBox.Text,out var p)?p:0,
        TrustHttpsBox.IsChecked==true,ShortcutBox.IsChecked==true,DatabaseBox.Text.Trim());
    private string SqlFingerprint()=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{SqlServerBox.Text,Database=DatabaseBox.Text,User=SqlUserBox.Text,Password=SqlPasswordBox.Password,Trust=TrustSqlBox.IsChecked}))));
    private void SetBusy(bool value)
    {
        busy=value;NextButton.IsEnabled=!value;BackButton.IsEnabled=!value;CancelButton.IsEnabled=!value;
        SqlPage.IsEnabled=!value;AccessPage.IsEnabled=!value;ReviewPage.IsEnabled=!value;CompletePage.IsEnabled=!value;MaintenancePage.IsEnabled=!value;
    }
    private void ShowPage(int value)
    {
        page=value;
        var panels=new[]{SqlPage,AccessPage,ReviewPage,ProgressPage,CompletePage,MaintenancePage};
        for(var i=0;i<panels.Length;i++)panels[i].Visibility=i==value?Visibility.Visible:Visibility.Collapsed;
        var titles=new[]{"Conecte ao banco do posto","Prepare o acesso ao LMC","Pronto para instalar","Instalando o LMC","LMC instalado","Gerencie esta instalação"};
        var descriptions=new[]{"Preenchemos os padrões do posto. Confira os campos, ajuste se necessário e teste a conexão.","O endereço e o login já vêm preparados. Você pode editar antes de continuar.","Confira os dados e o perfil da rede antes de continuar.","Acompanhe as etapas. O banco será apenas consultado para verificação.","Confira o acesso abaixo e abra a interface para começar.","Verifique o serviço, o certificado e o acesso pela rede."};
        PageTitle.Text=titles[value];PageDescription.Text=descriptions[value];Eyebrow.Text=value==5?"MANUTENÇÃO":"CONFIGURAÇÃO INICIAL";
        BackButton.Visibility=value is 1 or 2?Visibility.Visible:Visibility.Collapsed;
        NextButton.Visibility=value<3?Visibility.Visible:Visibility.Collapsed;
        NextButton.Content=value==2?"Instalar LMC":"Continuar →";
        StepNav.Visibility=value==5?Visibility.Collapsed:Visibility.Visible;
        var active=value switch{0=>0,1=>1,2 or 3=>2,_=>3};var steps=new[]{Step0,Step1,Step2,Step3};
        for(var i=0;i<steps.Length;i++){steps[i].Foreground=new SolidColorBrush(i==active?Colors.White:Color.FromRgb(163,198,180));steps[i].FontWeight=i==active?FontWeights.SemiBold:FontWeights.Normal;}
        StatusText.Text="";
    }
    private async Task<bool> CheckSql()
    {
        var options=Options();
        if(options.ValidateSql() is {} message){StatusText.Text=message;return false;}
        var fingerprint=SqlFingerprint();if(checkedSql==fingerprint)return true;
        SetBusy(true);StatusText.Text="";SqlResult.Text="Testando a conexão…";checkedSql=null;
        try
        {
            var result=await engine.CheckSql(options,s=>Dispatcher.Invoke(()=>SqlResult.Text=s));
            if(result.ExitCode!=0){StatusText.Text="Conexão não confirmada. Confira a mensagem junto ao botão Testar conexão.";SqlResult.Foreground=new SolidColorBrush(Color.FromRgb(160,61,40));SqlResult.Text=SetupOptions.ExplainSqlError(options.Redact(result.Output));return false;}
            SqlResult.Foreground=new SolidColorBrush(Color.FromRgb(18,107,75));
            checkedSql=fingerprint;SqlResult.Text="Conexão e estrutura verificadas. Nenhum dado foi alterado.";return true;
        }
        catch(Exception ex){StatusText.Text=options.Redact(ex.Message);SqlResult.Text="Não foi possível concluir o teste.";return false;}
        finally{SetBusy(false);}
    }
    private async void TestSqlClicked(object sender,RoutedEventArgs e){if(!preview)await CheckSql();}
    private async void NextClicked(object sender,RoutedEventArgs e)
    {
        if(busy||preview)return;
        if(page==0){if(await CheckSql())ShowPage(1);return;}
        if(page==1)
        {
            var options=Options();if(options.ValidateAccess() is {} message){StatusText.Text=message;return;}
            SetBusy(true);
            try
            {
                if(!await SetupOptions.IsLocalAddress(options.ServerAddress)){StatusText.Text="Esse endereço não pertence a este computador. Escolha o IP local mostrado na lista.";return;}
                ReviewText.Text=$"Banco: {options.SqlServer} / {options.Database}\nUsuário SQL: {options.SqlUser}\nCertificado SQL: {(options.TrustSqlCertificate?"próprio aceito, com criptografia":"validar identidade do servidor")}\nEndereço: {options.Url}\nUsuário da interface: {options.Operator}\nCertificado neste PC: {(options.TrustLocalHttpsCertificate?"preparar confiança no certificado gerado":"configurar manualmente depois")}\nAtalho na área de trabalho: {(options.CreateDesktopShortcut?"criar":"não criar")}";
                NetworkText.Text=await InstallerEngine.NetworkProfiles();ShowPage(2);
            }catch(Exception ex){StatusText.Text=ex.Message;}finally{SetBusy(false);}
            return;
        }
        if(page==2)await Install(false);
    }
    private async Task Install(bool update)
    {
        if(preview)return;
        var options=Options();
        if(!update&&checkedSql!=SqlFingerprint()){ShowPage(0);StatusText.Text="A conexão mudou. Teste novamente antes de instalar.";return;}
        installing=update?null:options;ShowPage(3);InstallLog.Clear();SetBusy(true);InstallProgress.IsIndeterminate=true;
        void Report(string s)=>Dispatcher.Invoke(()=>{InstallLog.AppendText((installing?.Redact(s)??s)+Environment.NewLine);InstallLog.ScrollToEnd();});
        try
        {
            var result=update?await engine.Update(Report):await engine.Install(options,Report);
            if(result.ExitCode!=0)throw new InvalidOperationException("A instalação não foi concluída. Confira as mensagens acima. Use Voltar para corrigir os dados.");
            installed=InstallerEngine.ReadInstalled();
            createdOperatorPassword=update?null:options.OperatorPassword;
            CopyCredentialsButton.Visibility=CredentialsHint.Visibility=update?Visibility.Collapsed:Visibility.Visible;
            SqlPasswordBox.Clear();OperatorPasswordBox.Clear();OperatorConfirmBox.Clear();installing=null;checkedSql=null;
            FinalUrl.Text=installed.Url;FinalUser.Text=$"Entre como {installed.Operator} com a senha da interface.";
            CertificateFingerprint.Text="Impressão digital: "+installed.Thumbprint;
            ShowPage(4);FinalDiagnosis.Text="Verificando serviço, certificado e acesso…";
            FinalDiagnosis.Text=await InstallerEngine.Diagnose(installed);
        }
        catch(Exception ex){StatusText.Text=options.Redact(ex.Message);Report(StatusText.Text);BackButton.Visibility=Visibility.Visible;BackButton.Content="Voltar";}
        finally{installing=null;SetBusy(false);InstallProgress.IsIndeterminate=false;}
    }
    private void BackClicked(object sender,RoutedEventArgs e)
    {
        if(busy)return;
        if(page==3&&InstallerEngine.ServiceExists()){try{installed=InstallerEngine.ReadInstalled();MaintenanceUrl.Text=installed.Url;ShowPage(5);}catch(Exception ex){StatusText.Text=ex.Message;}return;}
        ShowPage(page==3?0:Math.Max(0,page-1));
    }
    private async Task Diagnose()
    {
        if(installed is null||preview)return;SetBusy(true);StatusText.Text="Verificando o acesso…";
        try{var text=await InstallerEngine.Diagnose(installed);FinalDiagnosis.Text=text;MaintenanceDiagnosis.Text=text;StatusText.Text="";}
        catch(Exception ex){StatusText.Text=ex.Message;}finally{SetBusy(false);}
    }
    private async void DiagnoseClicked(object sender,RoutedEventArgs e)=>await Diagnose();
    private async void StartServiceClicked(object sender,RoutedEventArgs e)
    {
        if(preview)return;SetBusy(true);
        try{await InstallerEngine.StartService();}catch(Exception ex){StatusText.Text=ex.Message;SetBusy(false);return;}
        SetBusy(false);await Diagnose();
    }
    private async void UpdateClicked(object sender,RoutedEventArgs e)=>await Install(true);
    private void OpenLmcClicked(object sender,RoutedEventArgs e){if(installed is not null&&!preview)try{Process.Start(new ProcessStartInfo(installed.Url){UseShellExecute=true});}catch(Exception ex){StatusText.Text=ex.Message;}}
    private void CopyUrlClicked(object sender,RoutedEventArgs e){if(installed is not null){Clipboard.SetText(installed.Url);StatusText.Text="Endereço copiado.";}}
    private void CopyDiagnosisClicked(object sender,RoutedEventArgs e){Clipboard.SetText(MaintenanceDiagnosis.Text);StatusText.Text="Diagnóstico copiado.";}
    private void ExportCertificateClicked(object sender,RoutedEventArgs e)
    {
        if(installed is null||preview)return;
        var dialog=new SaveFileDialog{FileName="LMC-certificado-publico.cer",Filter="Certificado público (*.cer)|*.cer",DefaultExt=".cer",AddExtension=true,OverwritePrompt=true};
        if(dialog.ShowDialog(this)==true)try{File.Copy(installed.CertificatePath,dialog.FileName,true);StatusText.Text="Certificado público exportado. Importe-o como confiável nos computadores autorizados.";}catch(Exception ex){StatusText.Text=ex.Message;}
    }
    private async void TrustCertificateClicked(object sender,RoutedEventArgs e)
    {
        if(installed is null||preview)return;
        if(MessageBox.Show(this,"Adicionar às autoridades confiáveis deste computador o certificado do LMC instalado?\n\nImpressão digital:\n"+installed.Thumbprint,"Certificado deste LMC",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        try{InstallerEngine.TrustCertificate(installed);await Diagnose();}catch(Exception ex){StatusText.Text=ex.Message;}
    }
    private void CancelClicked(object sender,RoutedEventArgs e){if(!busy)Close();}
    private void WindowClosing(object? sender,CancelEventArgs e){if(busy){e.Cancel=true;return;}createdOperatorPassword=null;defaults=null;OperatorPasswordBox.Clear();OperatorConfirmBox.Clear();SqlPasswordBox.Clear();engine.Dispose();}
    public async Task RenderPreviews(string folder)
    {
        Directory.CreateDirectory(folder);defaults=SetupDefaults.Create([new("localhost",true)],["192.168.100.16"]);ApplyDefaults(true,true);
        ReviewText.Text="Banco: SERVIDOR / ATXDADOS\nUsuário SQL: sa\nEndereço: https://192.168.100.16:5443\nUsuário da interface: operador\nCertificado neste PC: preparar confiança no certificado gerado\nAtalho na área de trabalho: criar";
        NetworkText.Text="Ethernet: Privada — perfil compatível com a regra do LMC.";
        FinalUrl.Text="https://192.168.100.16:5443";FinalUser.Text="Entre como operador com a senha da interface.";
        CopyCredentialsButton.Visibility=CredentialsHint.Visibility=Visibility.Visible;
        FinalDiagnosis.Text="Exemplo de apresentação do resultado:\nServiço LMC: Running.\nPorta 5443: responde neste computador.\nHTTPS: LMC respondeu com certificado válido.\nConfirme o acesso também em outro computador da rede.";
        MaintenanceUrl.Text=FinalUrl.Text;MaintenanceDiagnosis.Text="Exemplo de diagnóstico:\nServiço LMC: Running.\nPorta 5443: responde neste computador.\nEthernet: Pública — o acesso dos outros computadores precisa de conferência do perfil da rede.";
        foreach(var value in new[]{0,1,2,4,5})
        {
            ShowPage(value);await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);UpdateLayout();
            var bitmap=new RenderTargetBitmap((int)ActualWidth,(int)ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(this);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(folder,$"tela-{value}.png"));encoder.Save(file);
        }
    }
}
