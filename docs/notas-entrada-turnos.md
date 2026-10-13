# Nota de entrada e troca de turno

Requisito solicitado pelo usuário em 06/10/2026. A **tela de revisão de notas é a primeira etapa, antes do LMC**. A interface e a API de consulta/prévia estão implementadas em `src/Lmc.Web`; a gravação conjunta de notas, movimentos, parcelas e histórico também foi implementada. Este documento separa o comportamento já disponível do fluxo completo planejado.

## Implementado nesta versão

- Consulta dos documentos vinculados a `LMCENTRADATANQUES`, agregados pela chave completa: filial, origem e documento. Cabeçalhos ausentes não entram na lista; continuam sendo escopo dos diagnósticos SQL.
- Busca por número, fornecedor ou combustível, filtros por qualquer data ou por campo específico, paginação e ordenação por atenção ou data recente. A pedido do usuário, a lista inicia com o primeiro e o último dia do mês anterior ao mês atual no navegador, período habitual de preparação do LMC. O intervalo é calculado ao abrir a página, inclusive na virada do ano. Atualizar os dados ou trocar a filial preserva os filtros escolhidos. O padrão “Qualquer data” inclui notas com pelo menos uma das datas no período; o operador pode ampliar ou limpar o intervalo para localizar lançamentos de outros meses.
- Triagem com intervalo configurável na tela (padrão exploratório: 5 dias), datas anteriores, campos ausentes e divergências internas. Turno de número zero aparece como **turno geral**, conforme confirmação do usuário, sem exigir que sua data cadastral coincida com a data da nota.
- Detalhe com emissão da nota (`DATA`), entrada da nota (`DTAEMISSAO`), data do LMC (`DTACONTA`), turno e parcelas por tanque. O usuário confirmou os significados de emissão/entrada e que o LMC considera a data operacional do lançamento.
- Seleção da data operacional, preservando por padrão o turno geral já vinculado à nota. Também é possível escolher um turno diário da mesma filial e data; observação opcional de até 80 caracteres, sem inventar chegada. O objetivo é facilitar o ajuste do lançamento, sem exigir justificativa digitada.
- Conferência automática calcula a redistribuição dos litros por produto/tanque entre origem e destino e lista livros dos produtos afetados desde a primeira data envolvida. Essa lista é conservadora: não afirma que todos os saldos estejam errados. Os saldos ainda não são recalculados.
- A revisão inicia em **Para conferir**. Nas notas coerentes do turno geral, a data de entrada registrada é sugerida para o LMC se estiver presente e não anteceder a emissão; o usuário pode ajustá-la. A conferência ocorre automaticamente, e **Salvar correção** é a única ação de confirmação, com aviso de revisão posterior do LMC. Depois do sucesso, a lista é atualizada. Os detalhes ficam disponíveis para expandir.
- A API relê a nota e seus vínculos e compara a versão antes de simular. A leitura do detalhe usa transação serializável curta, somente com SELECT. Campos alterados no formulário invalidam a prévia no navegador.
- Exportação JSON marcada como prévia não aplicada; o formulário de prévia não é persistido. A aplicação da correção grava os dados e o histórico, conforme [gravação e validação](correcao-gravacao.md).
- Continuação para uma consulta de livros existentes por data. Passar à segunda etapa não marca as notas como corrigidas nem homologa o LMC.

A interface foi testada com os dados reais da cópia de desenvolvimento. A troca de data/turno já pode ser aplicada. Produção e recálculo do LMC, novos recebimentos e exclusão permanecem como trabalho posterior.

Decisão confirmada: usar somente a estrutura existente do banco. A criação ou modificação de objetos está excluída; consultar, inserir, atualizar e excluir dados está incluído. O fluxo abaixo será implementado nas tabelas existentes.

## Objetivo

Permitir escolher a data operacional e o turno ao lançar uma nota de entrada e corrigir esses dois campos de uma entrada já registrada na própria tela. O operador deve conseguir ver a nota, sua distribuição entre tanques e os efeitos da mudança sem navegar por várias telas.

## Significado das datas

Esclarecimento do usuário em 06/10/2026: o campo `DATA` é a emissão da nota; `DTAEMISSAO`, apesar do nome, é a entrada. Para o LMC vale a data operacional do turno em que o lançamento foi atribuído.

| Nome na interface | Coluna na entrada | Propriedade atual da API |
| --- | --- | --- |
| Emissão da nota | `COMPROVANTES.DATA` | `documentDate` |
| Entrada da nota | `COMPROVANTES.DTAEMISSAO` | `issueFieldDate` |
| Data do LMC | `COMPROVANTES.DTACONTA`, coerente com movimentos e parcelas | `operationalDate` |

No turno geral, a data usada para o lançamento vem de `DTACONTA`; a data cadastral do turno geral não representa cada dia de movimentação. Nos turnos diários, o turno escolhido deve coincidir com essa data. A correção ajusta `DTACONTA` e o vínculo de turno, preservando emissão e entrada. A nova interface não altera os nomes das colunas. O significado acima é específico às entradas de notas: não deve ser aplicado indistintamente a campos chamados `DATA` em outras tabelas.

## Problema operacional relatado

O gerente registra as notas e pode selecionar uma data operacional ou um turno incorreto, deslocando a entrada cinco ou dez dias para a frente. O usuário informou que o combustível costuma vir de Manaus, com transporte de aproximadamente dois dias, e deu como exemplo emissão no dia 1 e recebimento no dia 2. Esses exemplos descrevem o processo; não fixam uma soma obrigatória de um ou dois dias à emissão.

A interface distingue emissão, entrada e data operacional conforme o mapeamento confirmado pelo usuário. Uma nota digitada dias depois da descarga pode estar correta se atribuída à data operacional efetiva. O instante de digitação continua sem mapeamento e não deve determinar sozinho o dia do recebimento.

## Tela de conferência de notas de entrada

A tela terá uma lista de notas com número, fornecedor, filial, produtos, volume, emissão fiscal, recebimento informado, data operacional, turno e motivo do alerta. Exibir origem e disponibilidade das datas: campo ainda não mapeado ou informação ausente não pode aparecer como chegada comprovada. As datas devem ficar lado a lado para facilitar a identificação do problema.

Permitir busca por nota, fornecedor e produto, ordenação pela diferença entre datas e filtros por tipo de pendência. O filtro de período deve permitir escolher emissão, recebimento, data operacional ou qualquer uma dessas datas. Uma nota deslocada para outro dia ou mês precisa continuar localizável pela emissão ou pelo número. A revisão de um dia deve oferecer acesso às notas de uma janela ampliada, informando o intervalo pesquisado; não restringir todas as buscas à data operacional que pode estar errada.

| Sinal para conferência | Evidência e tratamento |
| --- | --- |
| Intervalo fora do prazo esperado de transporte | Mostrar datas, quantidade de dias e referência logística da rota/cliente, quando definida; classificar como suspeita, sem inventar a chegada |
| Entrada anterior à emissão fiscal confirmada | Mostrar a fonte fiscal e conferir as datas e o vínculo do documento |
| Data operacional posterior à data real de trabalho selecionada | Avisar antes de gravar e permitir revisar o destino; registros previstos não devem ser confundidos com recebimentos já ocorridos |
| Data operacional divergente do recebimento comprovado | Mostrar a diferença e considerar o corte operacional, inclusive após meia-noite |
| Cabeçalho, movimentos ou parcelas por tanque em datas distintas | Identificar os registros divergentes e oferecer uma correção conjunta |
| Turno incompatível com a data operacional | O turno geral da filial vale para qualquer data operacional; turnos diários devem pertencer à data escolhida |
| Lançamento tardio com recebimento e data operacional coerentes | Identificar o atraso de digitação quando houver evidência, sem deslocar a chegada para o dia da digitação |

Os prazos de transporte servirão para triagem e poderão variar por cliente, rota ou fornecedor. Não assumir a mesma duração para todos, nem alterar uma data somente para reduzir perdas/sobras. O valor de cinco dias usado no diagnóstico SQL é um filtro exploratório derivado dos exemplos relatados, não uma tolerância normativa nem uma regra já homologada para a aplicação.

Na própria linha haverá **Revisar data e turno**. A revisão mostrará a situação atual, a data de recebimento comprovada ou informada pelo operador, a data operacional e os turnos possíveis de destino. Quando houver evidência suficiente, a aplicação poderá sugerir o destino e indicar a fonte; quando houver apenas o prazo provável de transporte, mostrará uma referência para investigação. A prévia indicará os dias que perdem e recebem o volume, o efeito nos saldos e alertas e os LMCs que precisam de revisão.

Exemplo de comportamento: nota emitida no dia 1, descarregada no dia 2 e atribuída por engano ao dia 10. A tela permite localizar a nota pela emissão, confirmar a descarga e mover sua data operacional e turno para o dia 2, preservando a emissão, os volumes e as medições. A mudança alcança cabeçalho, movimentos e parcelas pertinentes na mesma transação; o recálculo revela o efeito real nos dias afetados.

## Fluxo da interface

1. Selecionar ou cadastrar a nota e identificar filial, fornecedor, documento e itens.
2. Exibir separadamente a emissão (`DATA`), a entrada (`DTAEMISSAO`) e a data operacional do lançamento usada pelo LMC (`DTACONTA`).
3. Selecionar a data operacional na mesma tela, mantendo o turno geral quando já vinculado à entrada; oferecer também turnos diários da data escolhida.
4. Informar ou conferir as quantidades recebidas por produto e sua distribuição entre tanques.
5. Para uma nota já registrada, acionar **Alterar data e turno**, escolher a data e o turno de destino e visualizar o resumo de antes/depois.
6. Mostrar os movimentos e livros que precisam de recálculo ou revisão, junto às diferenças esperadas.
7. Aplicar a alteração conforme as permissões configuradas, registrar automaticamente o histórico com a observação quando informada e atualizar o resultado da conferência.

A ação deve estar disponível na lista de notas e no detalhe da entrada. Uma nota salva deve retornar ao mesmo contexto após a troca, com o turno atualizado e acesso ao histórico.

## Escopo da data operacional

A troca contempla turnos do mesmo dia e de outra data operacional. O usuário confirmou esse escopo em 06/10/2026, incluindo o exemplo de uma entrada após meia-noite atribuída ao último turno do dia anterior. A confirmação define a capacidade da aplicação; nenhuma nota específica foi alterada nesta etapa.

A mudança de data operacional preserva a data de emissão do documento fiscal e o instante real do recebimento informado. Cada informação tem finalidade distinta. A aplicação registra o antes/depois automaticamente; a observação do operador é opcional.

## Regras propostas

- O turno de destino deve pertencer à mesma filial da entrada. Turnos diários precisam coincidir com a data operacional escolhida; o turno geral não depende dessa data. Troca de filial é uma operação diferente.
- A quantidade total recebida e a distribuição por produto/tanque permanecem consistentes; trocar turno não cria uma segunda entrada nem muda volumes por si só.
- Identificar o documento pela chave completa, incluindo `ID_DB` quando a origem exigir. O número da nota isoladamente não identifica uma entrada.
- A operação deve considerar todos os movimentos associados. A nota pode ter vários itens e vários tanques; evitar joins que multipliquem volumes.
- Em turnos do mesmo dia, o total diário deve se conservar quando essa for a única alteração. Conferências por turno podem mudar.
- Ao mudar a data, simular o efeito nos dois dias e propagar a revisão de saldos para os dias posteriores dependentes, inclusive em mudança de mês ou ano. Preservar as medições físicas registradas; recalcular os saldos escriturais e reavaliar diferenças, sem inventar novas medições.
- Períodos conferidos ou encerrados devem seguir uma política explícita de revisão e preservação do histórico compatível com a estrutura existente. Os códigos de fechamento do legado ainda não foram interpretados.
- A rastreabilidade contempla autor, horário, turno/data anteriores e novos, livros afetados e observação quando informada. O armazenamento usa o histórico existente, conforme [gravação e validação](correcao-gravacao.md), sem criar tabelas ou colunas.
- Validar concorrência: a nota ou seus movimentos alterados após a simulação exigem nova conferência antes da aplicação.
- Repetir a mesma solicitação não deve duplicar movimentos ou recebimentos; troca para o turno atual retorna sem alteração.
- A validação e a persistência dos registros envolvidos devem ser atômicas. Uma falha não pode deixar metade da nota em cada turno.

## Persistência e uso pelo DOT

Usar a identidade e os vínculos das notas e movimentos nas tabelas existentes. Foi retirada a proposta de tabelas próprias e de registros paralelos para representar a troca. Atualizar de forma consistente os registros envolvidos, após mapear os efeitos das triggers. A alteração de turno será uma atualização da entrada e dos movimentos relacionados, sem excluir e recriar a nota para simular uma troca.

O serviço de negócio prevê `simular_troca_turno_entrada` e `trocar_turno_entrada`. A interface e as futuras ferramentas MCP do DOT chamarão esse mesmo serviço, com as mesmas permissões e validações. A API JSON já oferece prévia e aplicação; a exposição como ferramentas MCP permanece pendente.

## Critérios de aceite para a implementação

| Cenário | Resultado esperado |
| --- | --- |
| Selecionar data e turno ao lançar a nota | Nota e suas parcelas ficam associadas à data operacional e ao turno escolhidos |
| Trocar entre turnos do mesmo dia | Quantidades diárias preservadas e consultas por turno atualizadas |
| Selecionar turno de outra filial | Operação rejeitada com explicação |
| Selecionar turno diário de uma data diferente da escolhida | Operação rejeitada e seleção corrigida antes de aplicar |
| Corrigir a data mantendo o turno geral | Atualizar a data da nota, dos movimentos e das parcelas, mantendo o vínculo do turno; não depender da existência ou do fechamento de turno diário que não participa da operação |
| Nota distribuída entre vários tanques | Distribuição preservada e nenhum volume duplicado |
| Nota alterada enquanto a prévia estava aberta | Conflito informado e nova simulação solicitada |
| Repetir uma solicitação já aplicada | Retorno da operação existente sem duplicar efeitos |
| Período encerrado | Política de revisão aplicada e evidência anterior preservada pelo mecanismo a definir |
| Falha de persistência | Nenhuma alteração parcial nas entidades da operação |
| Trocar data operacional | Dois dias e saldos dependentes reavaliados; data fiscal e medições físicas preservadas |
| Mover uma entrada após meia-noite para o último turno anterior | Entrada atribuída ao destino escolhido, com histórico automático e sem duplicação; observação opcional |
| Mover entre meses ou anos | Encadeamento de saldos e situação dos períodos afetados revisados |
| Emissão no dia 1 e descarga confirmada no dia 2 | Datas distintas preservadas; não exigir entrada no mesmo dia da emissão |
| Descarga no dia 2 lançada por engano no dia 10 | Nota localizável pela emissão/número; revisão mostra origem, destino e impacto antes da correção conjunta |
| Recebimento no dia 2 digitado no dia 10 com data operacional correta | Não deslocar o recebimento para o dia da digitação |
| Prazo real de transporte maior que o habitual | Sinalizar para conferência, aceitando a data comprovada sem ajuste automático |
| Nota deslocada para o mês seguinte | Busca por emissão ou qualquer data permite encontrá-la mesmo fora do período operacional selecionado |
| Nota vinculada ao turno geral | Exibir “Turno geral” e mantê-lo selecionado ao escolher outra data operacional |

## Descobertas que orientam a implementação

O banco possui turno em `COMPROVANTES.ID_TURNOS` e `MOVPRODUTOS.ID_TURNOS`. As entradas por tanque em `LMCENTRADATANQUES` possuem documento e data, sem campo de turno. O cadastro `TURNOS` traz `DATATURNO`, `TURNO` e campos de situação. Os vínculos e as triggers estão registrados em [Mapeamento do banco herdado](xpert.md).

Foi confirmada a chave de `ITENSCOMPROVANTE` para o cabeçalho por `ID_COMPROVANTE`, `ID_FILIAL` e `ID_DB`, e para o produto por identificador e filial. A consulta de parcelas e itens deve evitar multiplicar o volume de uma nota com mais de um tanque ou item.

### Diagnóstico de datas em 06/10/2026

O [script de conferência](../sql/05-conferencia-datas-entradas.sql), executado com sucesso e somente leitura, localizou 1.000 documentos associados a 1.038 parcelas de `LMCENTRADATANQUES`, na filial 17581. Todos têm cabeçalho e movimento em `MOVPRODUTOS`. As datas operacionais dos cabeçalhos, movimentos e parcelas coincidem no conjunto analisado; isso comprova coerência interna desses campos, não a data real da descarga.

Entre `COMPROVANTES.DTAEMISSAO` e `DTACONTA`, há cinco documentos com intervalo positivo de pelo menos cinco dias, e 30 em que `DTACONTA` é anterior a `DTAEMISSAO`. Os cinco casos positivos são:

| Nota | ID_COMPROVANTE | DATA | DTAEMISSAO | DTACONTA | Intervalo DTAEMISSAO → DTACONTA |
| --- | --- | --- | --- | --- | --- |
| 1.337.740 | 541442 | 13/08/2026 | 14/08/2026 | 04/09/2026 | 21 dias |
| 1.158.177 | 118486 | 15/10/2024 | 17/10/2024 | 26/10/2024 | 9 dias |
| 1.349.377 | 560833 | 22/09/2026 | 24/09/2026 | 29/09/2026 | 5 dias |
| 1.349.379 | 560834 | 22/09/2026 | 24/09/2026 | 29/09/2026 | 5 dias |
| 1.349.380 | 560835 | 22/09/2026 | 24/09/2026 | 29/09/2026 | 5 dias |

Nesses exemplos, `ID_FILIAL` e `ID_DB` são 17581. São candidatos para revisão, sem diagnóstico de erro ou destino de correção definido. A tabela acima mantém os nomes de coluna para rastreabilidade; o significado foi posteriormente confirmado pelo usuário: emissão (`DATA`), entrada (`DTAEMISSAO`) e data operacional do LMC (`DTACONTA`). O intervalo diagnosticado é entre entrada e data do LMC, não tempo de transporte. `XMLENTRADAS` existe, mas não contém XML vinculado pela chave completa a esses 1.000 documentos nesta cópia.

Todos os 1.000 cabeçalhos e movimentos analisados apontam para `ID_TURNOS = 1`, com `DATATURNO = 25/03/2024` e `TURNO = 0`. O usuário confirmou em 06/10/2026 que esse é o **turno geral** usado nas entradas. A data cadastral desse turno não representa a data operacional da nota. A correção da data deve manter esse vínculo por padrão, sem forçar a transferência para um turno diário.

O banco está no nível de compatibilidade 100. O script de diagnóstico foi escrito e verificado nesse nível, sem propor sua alteração. O significado das datas de entrada e a função do turno geral foram esclarecidos pelo usuário; estados de fechamento e origem de novos recebimentos ainda dependem de mapeamento adicional. Nenhum documento foi alterado permanentemente nesta investigação.
