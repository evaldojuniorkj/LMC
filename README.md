# Interface LMC

Nova interface de interação com o Livro de Movimentação de Combustíveis no banco SQL Server `ATXDADOS` existente. O foco é facilitar o trabalho diário: consultar e conferir LMCs, identificar inconsistências, lançar e corrigir notas de entrada e alterar sua data operacional e turno. A proposta é uma interface no navegador, com futura operação por um DOT.

A interface utilizará o banco de dados do Xpert, sem precisar abrir o aplicativo desktop. Uma API realizará as consultas, validações e operações sobre os dados existentes, atendendo tanto à interface quanto à futura integração com o DOT. Os LMCs já registrados serão acessíveis pela interface e servirão de referência para conferir o comportamento das operações. O aproveitamento de rotinas existentes no banco dependerá do mapeamento e da validação de seus efeitos.

Regra confirmada pelo usuário: usar as tabelas e os campos que já existem e mexer somente nos dados. Não criar tabelas, adicionar ou remover campos, nem alterar seus nomes, tipos de dados, tamanhos ou demais definições. Os demais objetos do banco também serão preservados. A aplicação poderá consultar, inserir, atualizar e excluir registros nas tabelas existentes, conforme as operações de negócio.

## Situação em 07/10/2026

Interface e API implementadas em ASP.NET Core, conectadas aos dados reais do `ATXDADOS`: **revisão de notas → cadastro do LMC**. A revisão oferece busca, filtros, triagem, correção de data operacional/turno e redistribuição de litros entre tanques do mesmo combustível, conservando o total por produto e por nota. O cadastro oferece grade mensal e formulário com tanques, notas recebidas, encerrantes, aferições, vendas, medições e totais. A organização segue as telas de referência fornecidas pelo usuário, com nomes maiores, quebra de texto e números alinhados para leitura. Veja [funcionamento da conferência](docs/conferencia-lmc.md).

**Esta versão grava correções de notas, insere, refaz e exclui LMCs, individualmente ou em lote.** Inserir consulta a próxima data por combustível e preenche a abertura com o fechamento salvo do dia anterior, inclusive de livros antigos. Ao refazer, conserva o ID do livro, substitui seus detalhes pelas fontes conferidas e preserva os valores anteriores em histórico no campo existente `LMC.OBS`. A exclusão exige confirmação dos livros selecionados e conserva uma cópia no histórico existente `HISTORICOTURNORETRO`. Todas as partes são gravadas na mesma transação, sem alterar a estrutura do banco. Livros posteriores continuam sinalizados para conferência da abertura; não há recálculo automático em cadeia. Veja [gravação de LMC](docs/gravacao-lmc.md) e [correção das notas](docs/correcao-gravacao.md).

## Abrir a interface

**Instalação em outro computador do posto:** veja o [guia de produção](docs/producao.md). A versão 1.0.0 tem pacote Windows x64 com runtime, serviço automático, HTTPS e login de operador; as credenciais são configuradas somente no servidor de destino. Para gerar o ZIP, execute `./deploy/Publicar-LMC.ps1`. O modo de desenvolvimento abaixo continua restrito a este computador.

Requer o SDK .NET 10 e acesso Windows ao SQL Server local. Na pasta do projeto:

```powershell
.\iniciar.ps1
```

Abra [Revisão de notas](http://127.0.0.1:5080). O serviço fica limitado ao computador local. Encerre com `Ctrl+C`. A conexão usa `localhost / ATXDADOS`; para outra conexão, configure `ConnectionStrings__AtxDados` no ambiente ou `src/Lmc.Web/appsettings.Local.json` (ignorado pelo controle de versão). O padrão aceita o certificado do SQL Server local de desenvolvimento; a implantação deve usar certificado confiável e identidade restrita.

O fluxo é **Abrir a nota → Conferir a sugestão → Salvar correção**. A lista inicia nas notas sinalizadas do mês anterior. Nas entradas coerentes do turno geral, a data de entrada registrada é sugerida para o LMC, mantendo o turno geral; o usuário pode editar a sugestão. A conferência de impacto acontece automaticamente, sem gravar, e os detalhes podem ser expandidos. O botão de salvar informa que os LMCs afetados ficarão para revisão posterior. A chegada não é deduzida do prazo de transporte. Correções aplicadas permanecem no banco e no histórico existente; formulários ainda não aplicados são descartados ao recarregar.

### Verificação

```powershell
dotnet build src/Lmc.Web/Lmc.Web.csproj
dotnet run --project tests/Lmc.Checks/Lmc.Checks.csproj
# Com a aplicação em execução, contra esta cópia de desenvolvimento:
.\tests\verificar-api.ps1
# Ensaio com gravação e rollback, somente na cópia de desenvolvimento:
dotnet run --project tests/Lmc.Checks/Lmc.Checks.csproj -- --integration-rollback
# Redistribuição de litros entre tanques, sempre com rollback:
dotnet run --project tests/Lmc.Checks/Lmc.Checks.csproj -- --allocation-rollback
node tests/verificar-interface.mjs
# Inserção e reconstrução de LMC, sempre com rollback:
dotnet run --project tests/Lmc.Checks/Lmc.Checks.csproj -- --lmc-rollback
node tests/verificar-gravacao-interface.mjs
node tests/verificar-salvar-proximo.mjs
.\tests\verificar-lmc.ps1
.\tests\verificar-gravacao-lmc.ps1
# Exclusão completa e restauração por rollback:
dotnet run --project tests/Lmc.Checks/Lmc.Checks.csproj -- --delete-rollback
node tests/verificar-exclusao-interface.mjs
.\tests\verificar-exclusao-lmc.ps1
# Próxima data, abertura e exclusão em lote, sempre com rollback:
dotnet run --project tests/Lmc.Checks/Lmc.Checks.csproj -- --batch-rollback
.\tests\verificar-lote-lmc.ps1
# Avisos de dias anteriores sem LMC, somente leitura:
.\tests\verificar-continuidade-lmc.ps1
node tests/verificar-pendencias-interface.mjs
```

Os testes cobrem regras, medições, concorrência por versão, repetição segura da operação e falha após escrita parcial. Os ensaios `--integration-rollback` (datas/turnos), `--allocation-rollback` (distribuição entre tanques) e `--lmc-rollback` (livros) fazem gravações reais em transações sempre desfeitas. Os testes HTTP de LMC só consultam, calculam prévias ou verificam rejeições sem gravação. Algumas amostras são específicas da cópia analisada em 06/10/2026; correções reais do usuário não devem ser desfeitas para satisfazer testes antigos.

Para medir o salvamento e validar a proteção na cópia local substituída, execute `dotnet run --project tests/Lmc.Checks -p:UseAppHost=false -- --save-performance-rollback 4500 2026-07-31 1`. Os argumentos são filial, data e produto: escolha fontes válidas na cópia de teste. O ensaio insere/refaz com valores simulados e sempre executa rollback; não deve ser executado na base de produção.

O ensaio `--sources-rollback 4500 2026-09-26 1` verifica estabilidade da versão entre consultas repetidas e reconstrói um livro existente dentro de uma transação sempre desfeita. Aceita um arquivo JSON de `BookSaveRequest` como último argumento para reproduzir uma tentativa; a versão é obtida novamente e o identificador é exclusivo do teste. Use somente na cópia local de teste. Esse caso reproduziu um falso conflito causado pelas mesmas medições retornarem em ordem diferente.

## Levantamento e referência

- [Regras, fontes e verificações propostas](docs/regras-lmc.md)
- [Arquitetura e integração com o DOT](docs/arquitetura.md)
- [Mapeamento do banco herdado do Xpert](docs/xpert.md)
- [Leituras de tanques: dados encontrados e origem a validar](docs/medicoes-tanques.md)
- [Fontes e captura dos encerrantes de fechamento](docs/encerrantes-fechamento.md)
- [Lançamento de notas de entrada e troca de turno](docs/notas-entrada-turnos.md)
- [Gravação das correções, histórico e validação](docs/correcao-gravacao.md)
- [Conferência diária, medições e simulação do LMC](docs/conferencia-lmc.md)
- [Inserção e reconstrução dos LMCs nas tabelas existentes](docs/gravacao-lmc.md)

O acesso local por autenticação Windows foi verificado no servidor `AORUS-KZ`, banco `ATXDADOS`, utilizado como ambiente de desenvolvimento informado pelo usuário. O levantamento inicial foi somente leitura; os ensaios posteriores de gravação foram desfeitos por rollback.

## Consultas de levantamento

Os scripts abaixo apenas leem metadados e registros. Não executam rotinas do Xpert nem criam objetos no banco.

```powershell
sqlcmd -S localhost -d ATXDADOS -E -C -b -l 5 -t 30 -i sql/01-inventario-lmc.sql
sqlcmd -S localhost -d ATXDADOS -E -C -b -l 5 -t 30 -i sql/02-diagnostico-estrutural.sql
sqlcmd -S localhost -d ATXDADOS -E -C -b -l 5 -t 30 -i sql/03-perfil-calculos.sql
sqlcmd -S localhost -d ATXDADOS -E -C -b -l 5 -t 30 -i sql/04-origem-medicoes-tanques.sql
sqlcmd -S localhost -d ATXDADOS -E -C -b -l 5 -t 30 -i sql/05-conferencia-datas-entradas.sql
sqlcmd -S localhost -d ATXDADOS -E -C -b -l 5 -t 30 -i sql/06-fontes-encerrantes.sql
sqlcmd -S localhost -d ATXDADOS -E -C -b -l 5 -t 30 -i sql/07-consolearquivo-encerrantes.sql
```

O segundo script requer a seleção de uma filial no próprio arquivo e usa, por padrão, os 30 dias terminados na última data registrada para ela. Esse intervalo respeita a data da cópia disponível; não classifica os dias posteriores como faltantes. Os achados são candidatos à revisão, não uma declaração de conformidade fiscal.

O terceiro compara hipóteses de cálculo com os valores já gravados. A tolerância exploratória de 0,02 L usada nessa comparação não é uma tolerância normativa.

O quarto verifica configuração de medidor, leituras armazenadas e sua relação com os registros de estoque e LMC. A investigação confirmou leituras no banco, mas encontrou diferenças de datas e volumes entre as fontes. A identificação específica como TLS e a continuidade da captura sem o desktop permanecem pendentes.

O quinto lista candidatos à revisão de datas de notas de entrada. Na cópia analisada, foram identificados cinco documentos com intervalos de 5, 9 ou 21 dias entre `DTAEMISSAO` e `DTACONTA`. O usuário confirmou que `DATA` é emissão e `DTAEMISSAO` é entrada; o LMC segue a data operacional do lançamento (`DTACONTA`). A correção pode manter o turno geral já vinculado à nota. Os alertas são candidatos à revisão, não erros confirmados ou correções executadas.

O sexto compara os encerrantes dos LMCs com as fontes diárias e por turno; 538 de 540 fechamentos coincidem com `ENCERRANTESTURNOS`. O sétimo testa `CONSOLEARQUIVO`: os últimos encerrantes por bico/dia, usando `DATA`, coincidem com os 540 fechamentos. O arquivo passa a ser a principal fonte candidata, com conferência pelos turnos. O usuário confirmou que a automação já atualiza os encerrantes ao registrar vendas; a nova interface aproveitará essa alimentação, validando o corte e os estados dos registros.

O parâmetro `-C` aceita o certificado do servidor no ambiente local. A configuração de implantação deverá usar certificado confiável. A aplicação deverá utilizar uma identidade restrita; a sessão de descoberta tem privilégios administrativos.

## Instalador visual para Windows

Execute `deploy/Publicar-LMC.ps1` para gerar o ZIP técnico e o **LMC-…-Instalar.exe**, um instalador visual com a aplicação e o runtime incluídos. Os campos vêm preenchidos com padrões editáveis: instância SQL e IP locais detectados, banco `ATXDADOS`, usuário SQL `sa`, porta `5443`, operador `operador` e senha aleatória de 10 caracteres para a interface. O cliente informa a senha SQL, testa o banco e acompanha a instalação do serviço, HTTPS e acesso local. Ao encontrar um serviço LMC existente, a tela oferece manutenção e atualização preservando a configuração. Veja o [guia de produção](docs/producao.md).

Código da interface e do empacotamento: `src/Lmc.Setup`. Verificações sem instalar serviço: `dotnet run --project src/Lmc.Setup -p:PublishSingleFile=false -- --self-test`. A opção `--render-preview local/setup-preview` renderiza as telas com dados de exemplo para conferir o layout, sem executar a instalação.

## Próximas operações

O botão **Salvar e próximo** confirma a gravação e abre a próxima inserção do mesmo combustível, consultando a data no banco. A abertura vem do fechamento salvo do dia anterior; o fechamento do novo dia fica para informar. O botão de salvar comum continua voltando à lista. Se a confirmação não chegar, a mesma operação é consultada novamente antes de avançar. Se apenas a abertura do próximo falhar, é possível repetir a consulta sem gravar outra vez.

No editor do LMC, o encerrante final e a aferição de cada bico podem ser corrigidos. Vendas = final − inicial − aferição; o estoque escritural, as perdas/sobras e os valores de venda são recalculados. O salvamento grava os ajustes em `LMCBICOS` e registra o antes/depois no histórico já existente em `LMC.OBS`, sem modificar as fontes de automação nem a estrutura do banco. Ao reabrir, os encerrantes e aferições salvos são carregados. Confira a continuidade dos encerrantes nos livros seguintes após um ajuste.

Ao sair do fechamento físico, a tela avisa quando o tanque ultrapassa a referência operacional de 0,6% e procura uma correção possível de digitação. Uma sugestão só é aplicada por escolha explícita; casos ambíguos apresentam a faixa para conferência. É possível manter a medição informada. A validação de gravação continua no servidor.

Verificações dessas funções: `node tests/verificar-bicos-fechamento.mjs` e `dotnet run --project tests/Lmc.Checks -- --pump-rollback`. O segundo comando usa o último LMC de gasolina da filial de desenvolvimento 17581, verifica gravação/reabertura e desfaz o ensaio integralmente com rollback.

A revisão de notas é a entrada do fluxo. O cadastro mensal permite abrir um livro para refazê-lo ou inserir um dia ainda sem livro. Os próximos passos são validar a captura automática das medições, implementar a conferência e reconstrução em lote, impressão/exportação e ferramentas do DOT. A API de gravação já atende a interface.

O painel calcula perdas e sobras com o percentual sobre o estoque escritural (base confirmada pelo usuário em 09/10/2026), sem preencher uma medição para caber no limite. Na gravação, uma variação acima da referência exige justificativa nas observações do LMC, preservando o valor medido. Isso não altera a observação opcional da correção de notas. A homologação para emissão, a captura automática de medições e a análise de coincidências repetidas continuam pendentes. Veja as [regras de perdas e sobras](docs/regras-lmc.md#alertas-de-perdas-e-sobras-e-sugestões-de-correção).

No processo confirmado pelo usuário, a abertura vem do fechamento físico do dia anterior. O fechamento do dia é lançado manualmente ou obtido por medidor TLS (Gilbarco Veeder-Root na maioria dos clientes). A interface usará essa medição para comparar com o saldo escritural. Já foram identificadas leituras no banco; a origem específica e a correspondência com a data operacional precisam ser validadas, conforme o [relatório de medições](docs/medicoes-tanques.md) e o [fluxo de abertura e fechamento](docs/regras-lmc.md#abertura-e-fechamento-no-processo-informado).

Há outros protocolos ativos no cenário informado pelo usuário. A interface deverá suportar as diferentes integrações dos clientes, distinguindo medição de tanques de automação de bombas/bicos. A base inspecionada contém configurações separadas para essas funções e registros de automação; a identificação dos protocolos correspondentes ainda está em análise.

O escopo inclui lançamento de notas de entrada com escolha da data operacional e do turno, além da alteração desses dois campos em uma entrada já salva. O usuário confirmou a mudança entre datas, inclusive para atribuir uma entrada após meia-noite ao último turno do dia anterior. Haverá prévia dos efeitos nos movimentos e no LMC. A interface e o DOT usarão a mesma operação.

A conferência de notas destacará diferenças entre emissão, recebimento e data operacional e facilitará localizar entradas deslocadas cinco ou dez dias para a frente, inclusive em outro mês. O prazo de transporte informado pelo usuário será uma referência de triagem. A correção de data e turno ficará na própria lista, com prévia do impacto, preservando a emissão fiscal e as medições físicas.

Foi retirada a proposta de um schema e tabelas próprios. O banco existente contém triggers automáticas; seus efeitos são considerados nas correções e na gravação dos livros, sem alterar ou desativar esses objetos. A estrutura do banco permanece preservada.
