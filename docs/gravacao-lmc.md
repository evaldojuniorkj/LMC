# Inserção e reconstrução do LMC

Implementadas em 06/10/2026, com autorização do usuário para inserir e refazer livros existentes. A aplicação usa exclusivamente os dados das tabelas já existentes; não executa DDL, não modifica configurações e não desativa triggers. Os ensaios de gravação desta implementação foram desfeitos por rollback. As medições de teste não permaneceram no banco.

## Operação pela interface

1. Revise as notas e corrija sua data operacional e turno quando necessário.
2. Abra **Conferência do LMC**. A grade mensal mostra os livros gravados, com filtro por combustível e mês anterior como padrão.
3. Use **Inserir LMC** para abrir o dia seguinte ao último livro salvo da filial e do combustível escolhido. A data é consultada novamente no banco a cada clique. Sem filtro, começa pelo primeiro combustível com tanque ativo; trocar o combustível de uma nova inclusão consulta sua próxima data. Se não houver livro anterior, usa o início do mês selecionado. Use **Abrir** em uma linha para refazer aquele livro. A data pode ser alterada manualmente antes da gravação.
4. Confira entradas, bicos e aferições. Ao abrir um livro existente, abertura e fechamento de cada tanque vêm dos valores salvos em `LMCTANQUES`, inclusive nos livros feitos antes desta interface. Em um livro novo, a abertura vem automaticamente do fechamento salvo do dia imediatamente anterior, por tanque. O fechamento do novo dia permanece vazio para receber a medição.
5. A prévia é atualizada sem gravar. **Inserir LMC** ou **Salvar LMC refeito** só fica disponível quando o servidor valida os dados. O resultado informa o ID salvo e os livros posteriores a conferir.
6. Para remover um livro, use **Excluir** na grade ou **Excluir LMC** no editor. A confirmação identifica unidade, data, combustível, folha e quantidade de detalhes. **Cancelar** não altera dados; **Excluir este LMC** executa a remoção.
7. Para excluir em lote, marque os livros na primeira coluna e use **Excluir selecionados**. A caixa do cabeçalho seleciona todos os livros da lista filtrada. A confirmação mostra cada data, combustível e folha. O limite é de 200 livros da mesma filial; se qualquer um estiver bloqueado ou desatualizado, nenhum será excluído. Trocar filtro ou atualizar a grade limpa a seleção.

O editor mantém tanques, notas, bicos, observações e totais juntos, conforme as telas de referência do usuário. Leituras próximas ficam em um painel expansível, com data e fonte originais. A origem manual/eletrônica é declarada pelo operador; ela não certifica automaticamente que uma leitura veio do TLS.

**Origem do fechamento** usa duas opções de rádio: **Manual** e **Medidor eletrônico**. Manual fica selecionado por padrão quando não há origem registrada. Ao reabrir um livro com origem salva, a seleção é preservada. Para valores consultados no ATG ou em outro medidor, escolha Medidor eletrônico. A origem não precisa mais ser escolhida em uma lista inicialmente vazia.

O menu **Pendências do LMC** consulta todos os combustíveis da filial e permite filtrar um deles. A consulta é atualizada ao entrar no menu ou usar **Atualizar**, sempre somente leitura, sem alterar a estrutura ou corrigir dados automaticamente.

- **Aberturas e bicos:** compara `LMCTANQUES.QTDETANQUE` com `QTDECONCILIACAO` do mesmo tanque no dia anterior, e `LMCBICOS.ENCERRANTEABERTURA` com `ENCERRANTEFECHAMENTO` do mesmo bico no dia anterior. A referência é o mesmo combustível/filial e exatamente o dia anterior, inclusive na virada do mês/ano. Exibe data, equipamento, valores salvos e diferença com sinal. Compara na precisão de quatro casas usada pela leitura SQL; encerrantes aparecem com duas casas, revelando até quatro para diferenças menores que um centésimo. Registros duplicados, valores ausentes e final inferior ao inicial aparecem para conferência. Ausência do equipamento anterior não presume zero nem um erro de cadastro.
- **Entradas por tanque:** calcula abertura + entradas salvas em `LMCCOMPROVANTES` − vendas salvas em `LMCBICOS`, agrupadas por livro e tanque, e compara com o fechamento físico salvo. Aponta variações acima da referência já usada pela aplicação (0,6% do estoque escritural por tanque; escritural zero com diferença também exige conferência). Mostra todos os tanques do livro afetado. Sinais opostos entre tanques e presença de entrada geram **possível distribuição incorreta**, mesmo se o total do combustível estiver dentro da referência. Exemplo: 10 mil litros em A, mas descarga real de 5 mil em A e 5 mil em B, pode produzir −5 mil em A e +5 mil em B com total zero. Isso é um indício, não prova de descarga nem recomendação automática de divisão: conferir medições, notas, vendas e transferências. Sem medições físicas fiéis, a distribuição errada pode não ser detectada. **Revisar entradas** abre todas as notas daquele dia operacional; após corrigir a distribuição, conferir/refazer o LMC salvo para atualizar os seus detalhes.
- **Dias sem LMC:** considera a sequência do primeiro ao último livro salvo de cada combustível, inclusive outros meses e anos; não presume pendências fora desse histórico. Intervalos consecutivos são agrupados e **Preencher** abre o início do período escolhido no editor, sem gravar. Livros nos dias 1 e 3 geram uma pendência para o dia 2; a conferência de aberturas não usa o dia 1 como se fosse o dia 2.

**Conferir LMC** abre o dia e combustível do alerta para revisão. Essas pendências não bloqueiam a inclusão de novos livros; o formulário mantém as suas validações de medições e fontes.

Volumes, estoques e encerrantes são apresentados em litros inteiros, arredondados para leitura. Ao focar os campos de abertura e fechamento, aparece o valor exato para edição. A apresentação não arredonda as medições enviadas à API nem os cálculos; totais são calculados com a precisão original e arredondados apenas ao exibir. Preços, valores monetários e percentuais mantêm casas decimais.

## Fontes e campos gravados

| Destino | Conteúdo |
| --- | --- |
| `LMC` | Filial, produto, dia operacional, folha, valor das vendas, horário de criação e observações do operador |
| `LMCTANQUES.QTDETANQUE` | Abertura física confirmada, correspondente ao fechamento do dia anterior |
| `LMCTANQUES.QTDECONCILIACAO` | Fechamento físico informado ou selecionado pelo operador |
| `LMCBICOS` | Bico/tanque, encerrantes inicial/final, aferição, preço e volume vendido |
| `LMCCOMPROVANTES` | Uma linha por parcela atual de recebimento, com tanque, nota, volume, emissão fiscal e CNPJ do fornecedor |

O livro segue `DTACONTA` das entradas. A emissão fiscal vem de `COMPROVANTES.DATA`; `DTAEMISSAO` continua sendo a entrada, conforme confirmação do usuário. Canceladas são excluídas. Divergências entre nota, movimento, parcela, data ou turno impedem a gravação. O turno geral é aceito sem usar sua data cadastral como dia operacional.

O encerrante de abertura de cada bico fica fixo no encerrante de fechamento já salvo no LMC do dia imediatamente anterior, tanto na inserção quanto ao refazer. Diferenças apenas nas aberturas da automação ou dos turnos são avisos e não apresentam escolha de abertura. Se o livro existente tiver outra abertura, a tela informa que, ao refazer, ela será alinhada ao fechamento anterior; o log em arquivo conserva o valor antigo. Sem fechamento anterior disponível, a tela indica a origem alternativa utilizada (automação para novo livro ou abertura salva para livro existente), sem inventar um valor ou tratar ausência como zero. Fechamento anterior inválido ou repetido exige conferência.

O encerrante de fechamento proposto vem do último registro de `CONSOLEARQUIVO.DATA`, conferido com `ENCERRANTESTURNOS`; aferições e preços vêm dos turnos. Ausência, ambiguidade, contador decrescente, aferição inválida ou mudança de preço no mesmo dia bloqueiam a operação. Quando os fechamentos dos turnos diferem da automação, a grade mantém o aviso **diferença no fechamento** e já seleciona **Automação** por padrão. A opção **Turnos do dia** continua disponível para troca. O usuário pode seguir sem confirmar novamente a origem padrão; a interface a envia explicitamente na prévia e na gravação. A abertura fixa aparece separadamente. Não é exigida observação apenas por essa diferença. Dia sem abastecimentos exige turnos explicitamente sem movimento e continuidade com o fechamento do bico no livro anterior. Não se presume venda zero pela ausência de arquivo.

A escolha de origem do fechamento (`pumps[].counterSource`, `automation` ou `shifts`) é validada novamente no servidor; omissão ou origem inválida não liberam o livro com fechamentos divergentes. Essa escolha altera somente o final; o servidor não aceita substituir a abertura pela abertura dos turnos ou por um valor enviado pelo navegador. O final e a aferição continuam editáveis. Vendas, estoques e valores são recalculados com a abertura fixa. A origem, os conjuntos comparados e os valores efetivamente usados são guardados no arquivo de auditoria; `LMC.OBS` recebe somente o texto do operador. Nenhuma coluna ou tabela é criada e as fontes não são alteradas. Atualização das fontes, inclusive do fechamento anterior, invalida a conferência. Livros já salvos mantêm seus fechamentos gravados ao reabrir.

Durante a edição, o cabeçalho é compacto e somente a área dos dados tem rolagem. Os comandos **Salvar** e **Salvar e próximo** permanecem visíveis no rodapé, inclusive em telas menores. A área dos dados também aceita foco e rolagem pelo teclado. Encerrantes inicial e final são apresentados com duas casas decimais, mantendo a precisão original para cálculo, edição e gravação.

Ao refazer, os vínculos históricos, aferições e preços do livro são preservados e os encerrantes são conferidos com o arquivo. A abertura dos bicos segue o fechamento do dia anterior disponível, e o fechamento do dia é carregado do livro salvo. As entradas são recompostas a partir das parcelas atuais, incluindo as notas corrigidas. O valor das vendas é recalculado. O acumulado permanece nulo quando assim estava; se já preenchido, é recomposto com os valores registrados no mês.

A numeração observada usa `TIPONUMERACAO=2`, `USARNROSEQ=0`, `NUMERACAOUNICA=0` e `PAGINALMC=2`: dia 1 é folha 2. Inserções com outra configuração ficam bloqueadas até mapear a regra. Ao refazer, preserva-se a folha existente.

## Continuidade e medições

A abertura é herdada automaticamente de `LMCTANQUES.QTDECONCILIACAO` do dia imediatamente anterior, por tanque, inclusive quando o livro foi feito antes desta interface. O valor atual do banco tem prioridade sobre o histórico da interface; divergência nesse histórico gera aviso. Zero é preservado. Quando não existe um único fechamento anterior para o tanque, a abertura permanece vazia para conferência e preenchimento. Herdar um valor não certifica sua origem como medição física: valores inválidos continuam sujeitos às validações de gravação.

Ao reabrir qualquer livro, os campos mostram os valores atualmente salvos de abertura (`QTDETANQUE`) e fechamento (`QTDECONCILIACAO`) por tanque. O histórico da interface não é requisito para preencher esses campos. Valores zero são preservados; dados ausentes permanecem vazios. Se o histórico divergir do banco, os valores atuais são exibidos com aviso e a origem manual/eletrônica precisa ser conferida novamente. A origem das novas gravações fica no log e precisa ser conferida ao reabrir; não se deduz essa origem a partir dos volumes.

A abertura salva tem prioridade na exibição sobre uma herança diferente. Quando o fechamento conferido do dia anterior mudou, a tela permite **Usar fechamento anterior** por ação explícita e a validação exige essa continuidade antes de refazer. Abrir o livro não modifica nenhum registro. Medições ausentes, negativas, acima da capacidade ou com mais de quatro casas decimais são rejeitadas na gravação. Saldo escritural negativo é impedimento por tanque, mesmo quando outro tanque compensa o total do produto.

O percentual é apresentado como `(físico − escritural) / escritural × 100`, referência operacional de 0,6% do estoque escritural (base confirmada pelo usuário em 09/10/2026). Variação acima dessa referência exige observação no LMC; escritural zero com diferença também exige justificativa e não recebe percentual. O sistema mantém a medição informada, sem fabricar volumes para caber na faixa. Essa regra não torna obrigatória a observação nas correções de notas.

## Transação e histórico

### Referência da tabela de tanque na edição

A coluna **Tabela próxima · do tanque** usa o CSV explicitamente vinculado no menu **Tabelas dos tanques**. Cada vínculo distingue servidor/banco, filial, tanque e produto. Não se escolhe uma tabela automaticamente pela capacidade ou pelo combustível. O nome da tabela aparece na célula; tanques sem vínculo mostram **Sem tabela**. A comparação usa o escritural exato de cada tanque, antes do arredondamento visual, e mostra volume, altura e diferença. Empates mostram as duas posições; valores fora da faixa são identificados. Não há interpolação, preenchimento do fechamento ou alteração da solicitação de gravação. A consulta de referência ocorre em paralelo à conferência e uma falha no arquivo de tabelas não bloqueia o salvamento do LMC. Consulte [a configuração e o backup dos CSVs](tabelas-dos-tanques.md).

Os botões **Salvar LMC** e **Salvar e próximo** ficam acima dos dados, após os campos de combustível/data, visíveis enquanto o conteúdo do livro rola. A mudança conserva as validações e os mesmos eventos de gravação.

### Proteção da gravação

- Inserção ou reconstrução completa ocorre em uma transação serializável. Uma trava transacional do SQL Server serializa as gravações desta aplicação sem criar objetos.
- As fontes são consultadas novamente e comparadas à versão da prévia. Versão vencida retorna HTTP 409, sem gravar.
- A ordem das consultas é estável antes de calcular a versão e comparar as fontes. As medições de `MOVTANQUES`, `ESTOQUETANQUELMC` e `LEITURASTANQUES` são ordenadas por todos os campos no leitor, preservando duplicatas, valores nulos e valores inválidos originais. Uma mudança apenas na ordem de retorno do SQL não indica alteração de dados; mudanças de volume, tanque ou quantidade de registros continuam sendo detectadas.
- Refazer conserva o `ID_LMC` e o horário original do cabeçalho. Os detalhes daquele livro são substituídos; os IDs anteriores e seus valores ficam no arquivo de auditoria. Não foram encontradas referências de outras tabelas aos IDs desses detalhes na base inspecionada.
- Desde 09/10/2026, a auditoria de inserção/reconstrução vai para arquivos locais `.log`, com operador, horário, identificador da operação, medições, documentos, livros posteriores, observações e cópia XML dos valores anteriores. `LMC.OBS` recebe somente a observação digitada, sem `[LMCWEB-AUDIT-V1]` nem JSON automático. Não há migração ou limpeza dos registros antigos: a leitura continua compatível com o bloco legado; ao refazer explicitamente um livro, a observação anterior completa é preservada no log e o banco recebe apenas o texto humano atualizado.
- A versão e a trava transacional impedem reaplicar uma solicitação já gravada. Depois de uma resposta perdida, repetir a versão anterior retorna HTTP 409 e exige atualizar o livro. Não há busca de UUID em `OBS` nem recibo persistido no SQL para inserção/reconstrução. O UUID continua identificando a tentativa no arquivo de log.
- Triggers permanecem habilitadas. A aplicação compara os dados protegidos antes/depois, incluindo outros livros, leituras, estoques, parcelas e configuração, e confere os valores gravados conforme os tipos SQL existentes. Efeito fora do livro ou falha de representação da observação provoca rollback.
- A gravação HTTP exige cookie e token antiforgery. O operador no log corresponde ao usuário autenticado ou, no desenvolvimento local, à identidade do processo.

As verificações abrangentes de preservação têm custo de leitura e bloqueio e foram ensaiadas nesta cópia de desenvolvimento. Não há rotina automática de restauração pela tela; os dados anteriores estão disponíveis nos arquivos de auditoria para recuperação assistida.

A conferência de preservação calcula SHA-256 do XML integral de cada tabela no próprio SQL Server, devolvendo somente 32 bytes por tabela. Mantém os mesmos registros, campos, ordenação, transação e comparação antes/depois; não usa contagem ou checksum simples. Após gravar, relê as fontes do dia e confere o livro gravado, sem reconstruir novamente todo o contexto de edição. Para SQL Server anterior a 2016, mantém a leitura integral sequencial e calcula os mesmos resumos na aplicação, pois essas versões limitam a entrada de `HASHBYTES` a 8.000 bytes ([documentação Microsoft](https://learn.microsoft.com/en-us/sql/t-sql/functions/hashbytes-transact-sql)). Não altera índices, tabelas nem nível de compatibilidade.

O ensaio de 08/10/2026 na cópia local (SQL Server 2019, compatibilidade 100, filial 4500) mediu a rotina de inserção dentro da transação: 10.697 ms antes e 814 ms após a otimização; reconstrução: 754 ms. São medidas locais, sem tempo de commit ou navegação. O teste compara resumos no servidor e na aplicação, detecta alteração após 10.000 caracteres em outro livro, verifica repetição segura, versão vencida, reabertura e reconstrução. Todas as gravações de teste são desfeitas e o estado dos dados é comparado antes/depois.

### Arquivos da auditoria do LMC

- Desenvolvimento: `src/Lmc.Web/App_Data/audit-log/AAAA-MM-DD/` (relativo à raiz de conteúdo da aplicação).
- Instalação: pasta `audit-log` ao lado de `ProtectedConfig`, normalmente `%ProgramData%\LMC\audit-log`. `AuditLog:Directory` permite configurar outro destino. A preparação do serviço inclui permissão de escrita para sua identidade nessa pasta.
- Um arquivo JSON legível com extensão `.log` por tentativa, nomeado pelo UUID da operação e por um UUID próprio da tentativa. Uma nova tentativa nunca sobrescreve a anterior. O log não contém senha nem string de conexão.
- O arquivo é gravado e descarregado em disco antes do commit com `Status=prepared`. Após o commit recebe `committed`; rollback confirmado recebe `rolled_back`; resultado SQL incerto recebe `unknown`. Interrupções podem deixar `prepared`, que não prova gravação no banco.
- Se não for possível preparar o arquivo, a transação não é confirmada. Se apenas a atualização do resultado falhar depois do commit, a API informa que o LMC foi salvo e que o log ficou pendente; não instrui salvar novamente. Atualizações usam arquivo temporário e substituição atômica.
- Os arquivos ficam fora de `wwwroot`, não são publicados pelo site e precisam acompanhar o backup. Não há exclusão automática nem migração de auditorias antigas. O histórico de correções de notas e de exclusão permanece no mecanismo já existente.

A mudança foi validada sem acesso ao SQL Server: testes de observações, compatibilidade de leitura, escrita e confirmação do log, tentativa repetida, falha de arquivo e interface com API simulada. Os ensaios SQL com rollback não foram executados nesta alteração.

## Alcance desta versão

A exclusão remove `LMCCOMPROVANTES`, `LMCBICOS`, `LMCTANQUES` e o cabeçalho `LMC` selecionado por filial e ID, na mesma transação serializável. Usa a mesma trava de gravação dos livros, exige token antiforgery e compara a versão do livro e dos vínculos com a confirmação. Vínculo com envio externo no dia ou nos livros posteriores do mesmo produto impede excluir. Não depende de preencher medições ou corrigir o balanço para remover um livro.

Antes de confirmar a transação, a aplicação preserva uma cópia XML completa das quatro tabelas daquele livro, incluindo observações e histórico anterior, mais operador, horário, UUID e recibo. A cópia é serializada em JSON/Base64 e dividida em linhas de até 255 caracteres na tabela existente `HISTORICOTURNORETRO`, prefixo `LMCDEL1|UUID|sequência/total|conteúdo`. `ID_DB` usa a filial para essa convenção de livros, que não possuem origem `ID_DB`; turno e usuários de liberação ficam nulos. Não há tabela nova. Leitura de volta verifica que o histórico ficou completo. Não existe botão de desfazer: a recuperação exige assistência a partir dessa cópia.

A repetição do mesmo UUID e conteúdo retorna o recibo da exclusão já concluída. Conteúdo diferente com o mesmo UUID retorna conflito. Resposta de rede incerta mantém a confirmação aberta e repete exatamente a solicitação original. Notas fiscais, parcelas de entrada, movimentos, saldos, medições e outros livros são protegidos por comparação antes/depois. Efeitos inesperados das triggers causam rollback. A lista e a última data disponível para nova inserção são atualizadas ao concluir. Livros posteriores não são excluídos nem recalculados; a confirmação avisa que a sequência precisa ser conferida.

Na exclusão em lote, todas as versões e todos os bloqueios são validados antes de remover o primeiro registro. Os livros selecionados e seus detalhes são excluídos na mesma transação, com uma cópia de histórico por livro. O histórico principal inclui o recibo completo do lote para repetição segura com o mesmo UUID, mesmo que a ordem dos IDs mude. Livros selecionados são retirados do aviso de livros posteriores; os demais permanecem protegidos. O formato mantém o prefixo `LMCDEL1` e a tabela existente.

Refazer um dia não altera automaticamente os livros seguintes. A tela informa a sequência a conferir; reabra o dia seguinte e use o fechamento anterior quando for necessário atualizar a abertura, preservando seu fechamento físico. As pendências por correção de nota consideram o momento da última gravação pela interface. O encerramento global da fila e a reconstrução em lote permanecem pendentes.

Livros com vínculo de envio externo no dia ou período posterior são bloqueados. Impressão, exportação, envio fiscal e geração de notas de perdas/sobras não estão implementados. A precisão operacional é mantida nos tipos existentes; a apresentação fiscal dos encerrantes e a política de truncamento ainda exigem validação. A gravação no banco não equivale à homologação da emissão.

## API e verificações

- `GET /api/lmc/ledger?branch=17581&from=2026-09-01&until=2026-09-30`: grade dos valores gravados, sem multiplicar somas ao juntar detalhes.
- `GET /api/lmc/next?branch=17581&product=1&fallback=2026-09-01`: próxima data após o último livro salvo do combustível; `product` é opcional.
- `GET /api/lmc/pending?branch=17581`: consulta do histórico salvo da filial, por combustível, com último livro, `missingPeriods`, `inconsistencies` (continuidade dos tanques e bicos) e `tankReviews` (saldo individual dos tanques). Lê cabeçalhos, tanques, bicos e comprovantes na mesma transação de leitura. Não consulta nem modifica as fontes de automação.
- `GET /api/lmc/prepare?branch=17581&date=2026-09-29&product=1`: fontes, versão, medições, pendências e livros posteriores. `missingPeriods` contém intervalos anteriores sem livro (`from`, `until`, `days`), separados de `blockers`.
- `POST /api/lmc/preview`: valida o corpo informado e calcula o balanço sem escrever.
- `POST /api/lmc/save`: mesma seleção e versão da prévia, com `operationId`; exige `X-LMC-CSRF`.
- `GET /api/lmc/deletion?branch=17581&id=4428`: confirmação da exclusão por chave exata, com versão, contagens, livros posteriores e bloqueios.
- `POST /api/lmc/delete`: recebe `branch`, `bookId`, `version` e `operationId`; exige `X-LMC-CSRF` e devolve recibo persistido junto à exclusão.
- `POST /api/lmc/deletion-batch`: recebe `branch` e `bookIds`; consulta a confirmação completa sem excluir.
- `POST /api/lmc/delete-batch`: recebe `branch`, `books` (pares `bookId`/`version`) e `operationId`; exige `X-LMC-CSRF` e exclui o lote atomicamente.

Verificações executadas: regras do cálculo e da gravação; inserção e reconstrução completas em transações sempre desfeitas; preservação do ID; histórico anterior; entradas corrigidas; herança da abertura; repetição segura; conflito de versão; falha após inserção dos detalhes e restauração integral. Os testes de interface cobrem resposta atrasada, invalidação imediata, falha de comunicação, repetição com o mesmo identificador e ausência de medição. Os testes HTTP cobrem consulta, prévia e rejeições sem persistência. Comandos no [README](../README.md).

A exclusão foi ensaiada com `--delete-rollback`: remoção completa, cópia anterior, repetição segura, UUID com conteúdo divergente, versão vencida, livro ausente, vínculo de envio no livro e na sequência, sempre restaurando os dados por rollback. Os testes de interface verificam confirmação, cancelamento, resposta atrasada, bloqueio e repetição após falha. O teste HTTP de exclusão só consulta ou envia solicitações inválidas.

O ensaio `--batch-rollback` usa os livros mais recentes disponíveis e verifica a próxima data, a abertura herdada, a remoção conjunta, a cópia anterior, a repetição do lote e os bloqueios por versão e envio externo. Todo o ensaio é desfeito por rollback. Os testes de interface também cobrem data atualizada por combustível, seleção parcial e repetição do lote exato após falha de comunicação.

O ensaio `--pump-cutoff-rollback` simula diferenças nas aberturas e fechamentos de turnos dentro de uma transação. Confere que diferença somente na abertura não solicita escolha; as duas escolhas de fechamento preservam a abertura herdada; refazer acompanha alterações do fechamento anterior. O log em arquivo conserva os valores antigos. Também cobre ausência de escolha, versão antiga, gravação, reabertura e repetição segura, restaurando livros e fontes ao final. Os testes de interface cobrem abertura fixa antes e depois da escolha, recálculo, troca de fechamento com prévia atrasada e precisão dos encerrantes.
