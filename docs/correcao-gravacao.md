# Gravação das correções de entrada

Implementada em 06/10/2026. A operação permite corrigir data operacional, turno e distribuição dos litros entre tanques do mesmo combustível. O total da nota e o total de cada produto permanecem iguais. Datas e turnos são atualizados em `COMPROVANTES`, `MOVPRODUTOS` e `LMCENTRADATANQUES`; parcelas alteradas são recompostas na tabela existente. As triggers atualizam replicação e saldos calculados de tanques, com efeitos conferidos na transação. Não foram criados ou alterados objetos do banco.

## Fluxo disponível

1. A lista abre nas notas **Para conferir**, dentro do mês anterior. **Todas as notas** permanece disponível.
2. Abrir a nota. Para entradas no **turno geral**, sem divergência interna e com data de entrada presente e não anterior à emissão, essa data vem como sugestão para o LMC. A sugestão é identificada como baseada na entrada registrada e pode ser alterada; não determina que o lançamento atual esteja errado. O turno geral já vinculado é mantido. Sem esses requisitos, a data atual permanece.
3. A seção **Volume por tanque** permite editar os litros e escolher tanques ativos do mesmo combustível. **Dividir em outro tanque** acrescenta uma parcela e preenche o saldo que ainda falta distribuir. A soma é conferida por produto: falta, excesso ou volume inválido impedem salvar. Retirar uma parcela existente equivale a zerar seu volume; os litros devem ser redistribuídos nas demais. É possível corrigir só os volumes, mantendo data e turno.
4. A conferência é automática ao abrir ou editar data, turno, distribuição ou observação. O resumo mostra origem e destino. Histórico e impacto ficam recolhidos; recebimentos da mesma nota em livros de mais de um dia continuam com aviso visível.
5. **Salvar correção** confirma o destino e grava a entrada e o histórico na mesma transação. Não há etapa manual de gerar prévia nem caixa de confirmação adicional. A observação segue opcional, com até 80 caracteres e sem tamanho mínimo. Depois do sucesso, a tela retorna à lista atualizada com uma mensagem do resultado.

Os livros existentes não são recalculados nem têm seus recebimentos removidos automaticamente pela correção. O cadastro de LMC permite refazer cada livro após conferir suas fontes; não há reconstrução em cadeia nem resolução automática da fila conservadora de revisão. O aviso ao lado do botão **Salvar correção** informa a revisão posterior dos livros; o clique confirma essa sequência e envia `acceptBookReview=true`. Abrir uma nota, editar campos ou aguardar uma sugestão nunca aplica a correção.

Enquanto a conferência estiver pendente, o salvamento permanece desabilitado. Alterar qualquer campo invalida imediatamente a proposta; respostas antigas são descartadas. Falhas de versão exigem nova conferência. Uma gravação sem resposta confirmada congela os campos e oferece repetir exatamente a mesma operação, com o mesmo UUID, versão e conteúdo.

O usuário confirmou que as notas de entrada usam o **turno geral**, identificado nesta base por `TURNO = 0` (`ID_TURNOS = 1`, filial 17581). Sua data cadastral não limita a data operacional do lançamento. A consulta oferece os turnos gerais da filial e os turnos diários da data escolhida; apenas estes últimos exigem correspondência com `DATATURNO`. O ID do turno geral é consultado, não fixado no código. A seleção inicial preserva o turno geral que já está na nota.

Notas canceladas, documentos que não sejam entradas, vínculos divergentes, turnos de origem/destino com marca de fechamento e livros vinculados a envio externo bloqueiam a gravação. As marcas verificadas de turno são `DEFINITIVO`, `STATUSTURNO` e `DATAFECHAMENTO`; a conferência considera os turnos efetivamente vinculados à correção. Um turno diário fechado que não participa da operação não impede corrigir a data mantendo o turno geral. A reabertura de turnos não foi implementada, e o cadastro e as flags dos turnos não são modificados.

## Transação e conferência

`Corrections.cs` usa isolamento serializável e trava o documento e a consulta de identificação da operação. A versão lida na prévia é comparada de novo dentro da transação. Todas as escritas acontecem juntas; depois são relidos cabeçalho, movimentos e parcelas para confirmar o destino e a coerência. A versão inclui os tanques candidatos, para detectar mudanças de cadastro entre conferência e gravação.

Uma assinatura das colunas protegidas de cabeçalho, itens fiscais, movimentos, itens de movimentos e complemento de entrada é comparada antes/depois, excluindo data operacional/turno nas entidades apropriadas e `DATAREPL`. Sem redistribuição, as parcelas também fazem parte dessa assinatura. Com redistribuição, cada parcela é validada contra a proposta e o total original por produto; outras notas, livros, medições e cadastros são protegidos por uma assinatura adicional. Se dados protegidos mudarem por efeito das rotinas, a transação falha e é desfeita.

Nos ensaios desta base, reduzir `LMCENTRADATANQUES.QTDE` por `UPDATE` não reduziu o saldo em `QTDESTANQUES`, enquanto uma inserção acrescentou o volume ao saldo. Por isso, somente parcelas modificadas são excluídas e reinseridas, com as triggers habilitadas. Parcelas inalteradas conservam o ID; parcelas recompostas ganham novos IDs, com os anteriores registrados no histórico. O selo fiscal da parcela recomposta é preservado. A aplicação não atualiza `QTDESTANQUES` diretamente: confere que cada tanque variou exatamente pelo delta esperado, dentro de 0,02 L pela representação `real` legada, e que os demais saldos permaneceram intactos. Essa tolerância numérica não é um limite de perdas e sobras. Medições físicas não são ajustadas.

Os volumes aceitam até quatro casas decimais. A conversão ao tipo `real` existente é simulada antes da escrita e validada por releitura, para impedir que uma distribuição altere o total por arredondamento.

A rotina de gravação grava apenas dados nessas quatro tabelas: `COMPROVANTES`, `MOVPRODUTOS`, `LMCENTRADATANQUES` e `HISTORICOTURNORETRO`. As triggers permanecem habilitadas. O comportamento observado foi validado nos casos de desenvolvimento; não constitui homologação de qualquer outra versão do banco herdado.

## Histórico na estrutura existente

Foi reutilizada `HISTORICOTURNORETRO`, que já existe. A aplicação insere uma linha principal por correção, usando filial, origem, data/hora, turno/data de destino e `HISTORICO varchar(255)`. Quando há redistribuição, linhas complementares conservam todas as parcelas anteriores e posteriores. As colunas de usuário de liberação permanecem nulas: não se inventa uma autorização do aplicativo antigo.

O texto usa a convenção da aplicação `LMC1|operação|documento|origem,turno,início-revisão|assinatura|conta|produtos|motivo`. A identificação da operação é um UUID e a assinatura é SHA-256 do conteúdo da solicitação. O ator é a conta Windows que executa o serviço, exibida na confirmação e no histórico; não é autenticação individual de múltiplos operadores. O texto é relido após a inserção; truncamento ou conversão que não preserve caracteres causa rollback.

Por solicitação do usuário, a observação da correção é opcional. A propriedade JSON `reason` aceita ausência, `null`, texto vazio ou apenas espaços; esses casos são normalizados para texto vazio, inclusive na assinatura da operação. O histórico registra automaticamente o antes/depois, a conta e o horário mesmo sem observação. Nenhuma justificativa é inventada ou preenchida pelo sistema.

Uma repetição com o mesmo UUID e conteúdo retorna o recibo existente, sem nova alteração. O mesmo UUID com outro conteúdo retorna conflito. A linha de histórico é persistida na mesma transação das alterações, inclusive para permitir reconhecer uma operação confirmada no banco cuja resposta HTTP tenha sido perdida.

O prefixo `LMC1` distingue os recibos principais. A distribuição usa `LMCV1|operação|sequência/total|conteúdo`: JSON do antes/depois convertido em Base64 e dividido em trechos de 180 caracteres, respeitando o campo de 255 caracteres. Todos os trechos são gravados e relidos na mesma transação, com validação de sequência e completude. O detalhe da nota e a repetição da operação recuperam esse histórico. A assinatura da solicitação inclui a distribuição quando fornecida e conserva o formato anterior nas correções sem distribuição.

A aplicação não interpreta nem modifica linhas de outros processos. A compatibilidade dessas convenções com eventuais consumidores externos do histórico ainda deverá ser verificada antes de implantação em outro ambiente.

## Revisão dos livros

`LMCCOMPROVANTES` não possui a chave completa da entrada. Os recebimentos exibidos na prévia são candidatos por número da nota, filial, produto e tanque, com conferência adicional do CNPJ do fornecedor. Isso não autoriza alterar um livro apenas pelo número da nota.

Na nota 1.337.740 foram localizados 6.000 L no tanque 6 tanto em 14/08/2026 quanto em 04/09/2026, com fornecedor coincidente. A tela mostra os dois recebimentos para revisão. O início da fila considera também essas datas históricas, mesmo quando anteriores à data operacional atual da entrada. A persistência mantém os produtos e o início da revisão no histórico, sem depender de a nota continuar no mesmo dia depois.

## Rotas e segurança local

- `POST /api/notes/{filial}/{origem}/{documento}/preview`: confere data, turno e `allocations` opcional; retorna os deltas por dia/produto/tanque e a distribuição validada, sem gravar.
- `POST /api/notes/{filial}/{origem}/{documento}/apply`: aplicação com versão, UUID, observação opcional, ciência da revisão do LMC e `allocations` opcional. Cada parcela contém `parcelId` (nulo para nova), `productId`, `tankId` e `volume`. Todas as parcelas originais devem estar presentes; volume zero retira a parcela. O total é conservado por produto.
- `GET /api/notes/{filial}/{origem}/{documento}/history`: recibos de correção da nota.
- `GET /api/review-queue?branch=...`: correções que exigem revisão dos livros.
- `GET /api/security`: token antifalsificação associado ao cookie da sessão; a gravação exige `X-LMC-CSRF` válido.

O serviço continua limitado ao computador local. Não foi publicada uma aplicação em rede. Autenticação multiusuário e perfis de acesso são trabalho posterior; a gravação não deve ser exposta na rede com a identidade administrativa usada no desenvolvimento.

## Validação reproduzível

```powershell
# Somente em nossa cópia de desenvolvimento: executa gravações e sempre desfaz.
dotnet run --project tests/Lmc.Checks/Lmc.Checks.csproj -- --integration-rollback
# Redistribuição, correção de data e operação combinada, sempre com rollback:
dotnet run --project tests/Lmc.Checks/Lmc.Checks.csproj -- --allocation-rollback
# Com a aplicação em execução: consultas, prévias e tentativas rejeitadas.
.\tests\verificar-api.ps1
# Fluxo da interface com API simulada, sem acesso ao banco.
node tests/verificar-interface.mjs
```

Os ensaios abrangem troca entre datas, troca no mesmo dia, nota com três parcelas, repetição da operação, conflito de versão, histórico e bloqueio de turno com marca de fechamento. Incluem correção de data mantendo o turno geral em dia com turno diário fechado e em dia sem turno diário. Comparam livros, medições e cadastro de turnos também durante a transação, antes do rollback. Nenhuma correção de exemplo fica confirmada no banco. Inserções desfeitas podem consumir números de identidade, comportamento normal do SQL Server; os IDs não são reinicializados.

O ensaio de distribuição usa a nota 1349380 (documento 560835): divide 10.000 L em 6.000 L e 4.000 L, concentra novamente os litros, corrige somente a data e combina data com distribuição. Confere repetição, versão vencida, proteção de saldos e falha após escrita parcial do histórico. Comparações antes/depois do rollback confirmam que os dados originais permanecem intactos. Essas quantidades são exclusivamente cenários de teste.

O script `sql/08-ensaio-correcao-rollback.sql` documenta o primeiro ensaio: alterar somente o cabeçalho não move os registros associados. Diferentemente dos scripts 01 a 07, o script 08 executa UPDATE antes de desfazer a transação; não é um diagnóstico somente leitura.
