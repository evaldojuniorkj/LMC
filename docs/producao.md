# LMC 1.0.0 — instalação no posto

Este pacote atende a revisão de notas e a consulta, inserção, alteração e exclusão de LMCs no SQL Server existente. A estrutura do banco é preservada. Impressão, envio fiscal e captura automática do ATG ainda não fazem parte desta versão.

O pacote é para Windows x64 compatível com .NET 10 e inclui o runtime: não é necessário instalar SDK ou Visual Studio. Execute a instalação no computador que ficará ligado e tem acesso ao SQL Server. Reserve um nome/IP estável para esse computador.

## Requisitos antes da instalação

- Windows de 64 bits compatível com .NET 10. Prefira Windows 11 ou Windows Server 2019/2022/2025 atualizados; Windows 7 e 8.1 não são compatíveis. Confira a [lista de versões e dependências da Microsoft](https://learn.microsoft.com/en-us/dotnet/core/install/windows#supported-versions) para outras edições, inclusive Windows 10 LTSC/Enterprise e servidores mais antigos.
- PowerShell de 64 bits, versão 5.1 ou superior, aberto como Administrador. O Windows PowerShell que acompanha as versões de Windows acima pode ser usado; PowerShell 7 é opcional.
- SQL Server existente, com o banco `ATXDADOS`, acessível desse computador e com autenticação SQL habilitada. Ele pode estar na mesma máquina ou em outro servidor da rede. Tenha o nome/instância ou endereço e as credenciais do banco.
- Nome ou IPv4 fixo para o computador da aplicação e perfil de rede Privado ou Domínio para acesso dos demais computadores. A porta HTTPS 5443 deve estar disponível.
- Navegador atualizado nos computadores que usarão a interface. Após instalar, configure neles a confiança no certificado conforme a seção HTTPS.

O pacote já inclui .NET e ASP.NET Core. Não precisa instalar IIS, Visual Studio, Node.js ou o programa Xpert. A instalação usa o banco que já existe no posto.

## Instalar pela interface visual

Abra o arquivo **LMC-…-Instalar.exe** no computador que ficará ligado no posto e aceite a solicitação de administrador do Windows. É um único executável: inclui a aplicação, o instalador e os runtimes, sem precisar extrair ZIP ou digitar comandos. Esta distribuição ainda não possui assinatura digital de editor; confira a origem e o SHA256 fornecido. Se a política da empresa exigir programas assinados, o responsável pela distribuição precisará assinar o executável com um certificado de assinatura de código aprovado.

1. **Conexão com o banco:** confira o servidor/instância detectado, o banco `ATXDADOS` e o usuário `sa`; todos são editáveis. Informe a senha SQL e clique em **Testar conexão**. O teste consulta a estrutura do banco escolhido sem alterar dados. A opção de aceitar o certificado próprio do SQL na rede interna já vem marcada para o padrão dos postos; pode ser desmarcada quando a instância usa um certificado confiável. A criptografia permanece ativa.
2. **Acesso à interface:** o instalador preenche o IP deste computador, a porta **5443**, o usuário **operador** e uma senha aleatória de **10 caracteres**, igual na confirmação. Use **Copiar senha da interface**, **Gerar outra senha** ou edite os campos. O endereço precisa pertencer à máquina da aplicação, mesmo que o SQL esteja em outro servidor. As opções de preparar o certificado HTTPS neste Windows e criar o atalho vêm marcadas e podem ser alteradas.
3. **Conferir e instalar:** confira o resumo e o perfil da rede, depois clique em **Instalar LMC**. O instalador protege a configuração, instala o serviço automático e cria a regra da rede local. Ele não transforma redes Públicas em Privadas nem libera o firewall para qualquer origem.
4. **Tudo para começar:** acompanhe o teste de serviço, porta e HTTPS. Antes de fechar uma instalação nova, use **Copiar login e senha** para guardar o acesso. Use **Abrir LMC**, **Copiar endereço** ou **Exportar certificado público**. Os testes são executados no próprio servidor; o acesso de outra estação precisa ser confirmado nessa estação. Em caso de rede Pública, a tela explica por que a regra não permite o acesso dos demais computadores.

**Restaurar padrões** repõe os campos da etapa atual. Voltar ou avançar entre etapas mantém as edições do cliente. A detecção acontece no computador onde o instalador é aberto: nenhum IP específico de outro posto fica fixo. Se houver várias instâncias SQL sem uma escolha clara, selecione a correta na lista ou digite o endereço. O pacote não contém senha SQL; a senha inicial da interface é gerada ao abrir cada instalação nova.

Se o serviço LMC já existir, o executável abre a **tela de manutenção**. Ela permite verificar o acesso, iniciar o serviço parado, preparar ou exportar o certificado e atualizar a aplicação preservando a conexão e os operadores. A atualização reinicia o serviço e deve ser feita quando não houver lançamentos em andamento. Essa tela também atende instalações feitas anteriormente pelo PowerShell.

As senhas informadas na tela são enviadas ao processo de instalação por um pipe privado, nunca pela linha de comando ou por arquivo JSON em texto. A configuração final permanece criptografada pelo Windows. Os arquivos extraídos para executar a instalação são temporários; o serviço utiliza a cópia instalada em Arquivos de Programas.

O instalador prepara o certificado HTTPS no armazenamento do computador e concede à conta `LocalService` somente leitura da chave privada desse certificado. O serviço usa o certificado exato, identificado pela impressão digital, sem reimportar o PFX ao iniciar. A instalação só é concluída após o serviço responder ao teste HTTPS com esse certificado. Se houver falha, os eventos recentes do LMC são apresentados na própria tela.

Se um pacote anterior falhou com **“O serviço encerrou ao iniciar”** e o evento do Windows mostra **`CryptographicException: Acesso negado`** em `ImportPfx`, feche o instalador antigo e use o executável atualizado. Executar novamente o pacote antigo como administrador não muda a identidade `LocalService` do serviço. Se aparecer a manutenção, escolha **Atualizar aplicação**; caso contrário, refaça a instalação. Na atualização, o certificado, as credenciais e a configuração existentes são preservados. Uma instalação nova gera outro certificado público, que deve ser importado nos clientes.

## Instalar pelo PowerShell (alternativa técnica)

1. Extraia o ZIP completo em uma pasta do servidor. Tenha um backup recente do `ATXDADOS`, feito pela rotina de backup do posto.
2. Abra o PowerShell como Administrador nessa pasta.
3. Execute `./Instalar-LMC.ps1`. Se o Windows identificar o arquivo como baixado, confira a origem e o SHA256 do pacote e use as propriedades do arquivo para desbloqueá-lo. O instalador não muda a política de execução do Windows.
4. Informe o servidor/instância do SQL Server e o nome ou IPv4 fixo do computador da aplicação. O banco padrão é `ATXDADOS`, editável com `-Database`; o usuário SQL padrão é `sa`, editável com `-SqlUser`. O banco escolhido precisa existir e conter as tabelas compatíveis com o LMC.
5. Digite a senha SQL diretamente no instalador. Depois **crie uma senha nova** para o usuário `operador` da nossa interface, com pelo menos 10 caracteres. Não é uma senha de operador já cadastrada no Xpert: você escolhe essa senha agora e a usa para entrar pelo navegador. Isso não muda a senha do banco. As senhas não são passadas na linha de comando.

Nessa alternativa, inicie pelo `Instalar-LMC.ps1`, com o ZIP inteiro extraído. O arquivo `app\Lmc.Web.exe` é o programa que o serviço executará após receber a configuração; ele não é o instalador visual.

Se o Windows informar que a execução de scripts está desabilitada, após conferir a origem e desbloquear o arquivo baixado, use `powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\Instalar-LMC.ps1` no PowerShell aberto como Administrador. Essa opção vale somente para esse processo e não muda a política permanente do computador. Políticas impostas pela organização continuam valendo e devem ser tratadas pelo responsável de TI.

Exemplo, adaptando os endereços ao posto:

```powershell
./Instalar-LMC.ps1 -SqlServer 'SERVIDOR\INSTANCIA' -ServerAddress '192.168.1.10'
```

O instalador verifica conexão e colunas principais sem alterar o banco. Depois instala o serviço `LMC` com início automático, executado como `LocalService`, e libera somente a porta HTTPS 5443 para a sub-rede local nos perfis Privado/Domínio do firewall. Não publica o SQL Server nem abre acesso pela internet. A conta SQL informada determina os privilégios no banco; o instalador não cria contas, não concede permissões e não reduz os privilégios existentes de `sa`.

Na alternativa pelo PowerShell, a conexão SQL usa criptografia e validação do certificado por padrão. Se a instância usa certificado próprio sem cadeia de confiança, configure um certificado confiável no SQL Server; para instalações legadas que exigem esse comportamento, o parâmetro explícito `-TrustSqlCertificate` mantém a criptografia e dispensa a validação da identidade do certificado SQL. A interface visual já marca essa opção no perfil padrão dos postos. Essa opção se refere ao SQL Server, não ao certificado HTTPS da interface.

Se a verificação retornar **código -2146893019**, o Windows não confia na autoridade emissora do certificado SQL. Isso ocorre antes de validar o acesso ao banco. Configure a cadeia de confiança do certificado ou, em uma instalação na rede interna que usa certificado próprio, repita explicitamente com `./Instalar-LMC.ps1 -TrustSqlCertificate`. A opção também existe nos pacotes anteriores e não requer novo download. Mantenha o mesmo servidor/instância e as credenciais corretas; o instalador repetirá a verificação antes de registrar o serviço. Veja a [explicação da Microsoft](https://learn.microsoft.com/en-us/troubleshoot/sql/database-engine/connect/error-message-when-you-connect).

## HTTPS nos computadores clientes

É possível fornecer um certificado PFX emitido para o nome/IP do servidor com `-CertificatePath 'C:\certificados\posto.pfx'`. A senha do PFX é solicitada sem aparecer na tela.

Sem esse parâmetro, o instalador gera um certificado próprio válido por dois anos e exporta **somente a parte pública** para `LMC-certificado-publico.cer`. Confira a impressão digital apresentada no servidor. Em cada computador autorizado, o responsável de TI deve importar esse certificado público para **Autoridades de Certificação Raiz Confiáveis** do Windows e conferir a mesma impressão digital. Distribua apenas o `.cer`; não copie o `.pfx` ou a configuração privada para os clientes. Se a política do posto não admite certificados próprios, use o PFX emitido pela autoridade aprovada.

Abra `https://NOME-OU-IP-DO-SERVIDOR:5443`. O navegador deve reconhecer o certificado antes de informar credenciais. Entre como `operador` com a senha criada na instalação. As operações registram o nome do operador autenticado no histórico existente.

No próprio servidor, também pode abrir **`https://localhost:5443`**. Digite o endereço completo com `https://`: a porta 5443 atende HTTPS. O serviço aceita IPv4 e IPv6 (com alternativa IPv4 em sistemas sem IPv6), incluindo o endereço local `::1` usado pelo Windows para `localhost`. A atualização aplica esse suporte também a instalações anteriores, preservando a porta e o certificado. Nos outros computadores, use o IP/nome do servidor, pois `localhost` sempre indica o computador em uso.

## Confirmar a instalação

- O serviço `LMC` deve estar em execução e a tela deve mostrar **Produção · rede local · v1.0.0** e a filial esperada.
- Confira um livro já existente, suas medições e bicos, e uma nota de entrada conhecida. Essa conferência não grava dados.
- Faça o primeiro lançamento real de forma acompanhada e reabra o livro para conferir. Os ensaios da versão foram feitos na cópia de desenvolvimento; a base do posto pode ter configurações, volumes, vínculos e triggers diferentes. O instalador verifica colunas principais, mas não homologa automaticamente todas essas diferenças.
- Para diagnosticar: `Get-Service LMC`. Os erros do serviço são registrados em **Visualizador de Eventos → Logs do Windows → Aplicativo**, origem `Lmc.Web`.

## Arquivos e manutenção

O programa fica em `%ProgramFiles%\LMC\1.0.0`. A configuração SQL e a senha do PFX ficam em `%ProgramData%\LMC\configuration.bin`, criptografadas pelo Windows (DPAPI da máquina) e com acesso restrito ao serviço e aos administradores. A senha da interface é armazenada como hash PBKDF2, nunca como texto. A pasta `keys` guarda as chaves das sessões. Esses arquivos são próprios da aplicação e não criam tabelas no SQL Server.

A configuração protegida **não é portátil entre computadores**. Para mover de servidor, execute novamente a instalação e informe as credenciais no destino. O instalador não inclui senhas no ZIP. Se uma instalação falhar antes de registrar/iniciar o serviço, corrija a causa e execute novamente; as pastas reconhecidas do instalador podem ser reaproveitadas.

Se a tentativa anterior parou com **“Não há suporte para a palavra-chave: DataSource”**, use o pacote corrigido e execute novamente `Instalar-LMC.ps1`. A falha era na montagem da conexão pelo instalador, antes de registrar o serviço ou acessar o SQL Server. Não é necessário apagar o banco nem as pastas deixadas pela tentativa anterior. Essa repetição pedirá novamente a senha SQL e a criação da senha da interface.

Para atualizar, extraia a nova versão e execute `./Atualizar-LMC.ps1 -Version '1.0.0'` como Administrador, quando os operadores terminarem os lançamentos. O script verifica a conexão, preserva configuração e certificado, copia a versão para uma pasta nova e reinicia o serviço. A pasta anterior permanece no disco; o caminho anterior do serviço fica em `%ProgramData%\LMC\previous-service-path.txt`. Uma falha ao iniciar a nova versão restaura o caminho anterior. Voltar à versão anterior do programa não desfaz dados já gravados no banco.

O certificado precisa ser renovado antes do vencimento. A aplicação tem limitação de tentativas de login e cookies protegidos por HTTPS. A criação/remoção de operadores continua a cargo do responsável da instalação.

## Trocar a senha do operador

No canto superior direito, abra o menu com seu nome e escolha **Trocar senha**. Informe a senha atual, uma nova senha de **10 a 256 caracteres** e a confirmação. A troca mantém a sessão atual; os demais acessos desse operador precisam entrar novamente. Os campos são apagados ao fechar a janela, e a aplicação confirma quando a gravação termina. A opção aparece na instalação com autenticação; o modo local de desenvolvimento, sem login, não tem uma senha de operador para alterar.

Os hashes dos operadores ficam em `%ProgramData%\LMC\operator-access\accounts.bin`, criptografados por DPAPI. Somente essa pasta de operadores recebe escrita para `LocalService`; a conexão SQL e o certificado continuam nas áreas de configuração com leitura restrita. A gravação é atômica, e uma atualização preserva senhas já trocadas. O código de preparação cria esse arquivo a partir dos operadores existentes quando ele ainda não existe. O app exige o arquivo nas instalações protegidas, sem voltar silenciosamente à senha antiga. Versões anteriores à funcionalidade não leem esse arquivo; uma reversão para essas versões exige tratar o acesso com o responsável pela implantação.

Referências técnicas: [serviço Windows e publicação com runtime](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/windows-service?view=aspnetcore-10.0), [autenticação por cookie](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/cookie?view=aspnetcore-10.0).
