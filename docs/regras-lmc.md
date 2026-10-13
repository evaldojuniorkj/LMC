# Referências e regras do LMC

Pesquisa realizada em 06/10/2026. Este documento diferencia exigências identificadas nas fontes, propostas de software e pontos ainda não homologados.

Estas referências orientarão as conferências e operações da nova interface de interação com o LMC. Os dados e livros existentes no `ATXDADOS` ajudam a interpretar o histórico e comparar resultados. A distribuição das operações entre a API e os recursos existentes no banco será definida conforme o mapeamento e a validação de seu comportamento, sem depender do aplicativo desktop Xpert.

A implementação deve usar a estrutura existente, sem criar tabelas ou modificar os objetos do banco. O usuário confirmou que a aplicação poderá consultar, inserir, atualizar e excluir os registros necessários às operações de negócio.

## Referências normativas

A [página da ANP sobre LMC](https://www.gov.br/anp/pt-br/acesso-a-informacao/perguntas-frequentes/agente-economico/livro-de-movimentacao-de-combustiveis) continua indicando a Resolução ANP nº 884/2022. Ela também esclarece a exclusão do GNV, a numeração sequencial das folhas e a assinatura do representante legal nos termos de abertura e fechamento.

A [Resolução ANP nº 884/2022, reproduzida pelo Ministério Público da Bahia](https://ceaf.mpba.mp.br/wp-content/uploads/2022/10/Resolucao-884-2022-da-ANP-Agencia-Nacional-do-Petroleo-Gas-Natural-e-Biocombustiveis-BR.pdf) estabelece:

- Registro diário, inclusive sem movimento; escrituração eletrônica ou manuscrita.
- Compras documentadas, vendas por bico, estoques, outras entradas/saídas e preços.
- Disponibilidade do LMC e documentos fiscais no estabelecimento por seis meses; regra transitória de preservação dos livros antigos no art. 6º.
- Apuração das causas e justificativas nas observações para variações superiores a 0,6% sem comprovação da movimentação.
- Registro de intervenções em encerrantes, mudanças na medição e transferências internas.
- Substituição da Portaria DNC nº 26/1992 e da Resolução ANP nº 23/2004.

O anexo define as relações:

```text
Venda por bico = encerrante final − encerrante inicial − aferições
Disponível = abertura física + recebimentos
Estoque escritural = disponível − vendas
Perdas/ganhos = fechamento físico − estoque escritural
```

Os campos de encerrantes do anexo desprezam decimais. Preservar a leitura original e validar a transformação aplicada à escrituração, sem arredondar indiscriminadamente o banco.

A orientação da ANP é observar as perdas diariamente, conforme a [pergunta 27 da seção Posto revendedor](https://www.gov.br/anp/pt-br/acesso-a-informacao/perguntas-frequentes). Um acompanhamento mensal adicional não substitui essa conferência.

## Abertura e fechamento no processo informado

O usuário esclareceu em 06/10/2026 que a abertura corresponde ao estoque final do dia anterior. O fechamento físico é informado manualmente ou obtido de medidor eletrônico TLS, da Gilbarco Veeder-Root na maioria de seus clientes. Essas origens fazem parte do processo; a forma de receber as leituras do equipamento ainda precisa ser mapeada.

```text
Abertura do dia = fechamento físico do dia anterior
Estoque escritural = abertura + recebimentos − vendas
Perda ou sobra = fechamento físico medido − estoque escritural
```

As relações acima descrevem o fluxo básico. Outras movimentações documentadas, quando existentes, precisarão ser mapeadas para compor o saldo corretamente.

A interface deverá apresentar a abertura com a referência ao fechamento anterior, o cálculo escritural e o fechamento medido. A abertura será apurada na mesma filial e por tanque/produto, respeitando os vínculos históricos e consolidando os tanques do produto. Se faltar o fechamento do dia anterior, sinalizar a pendência: não copiar silenciosamente o último valor de uma data mais antiga. O primeiro dia disponível exige uma abertura comprovada a mapear nos dados existentes.

O aproveitamento do fechamento anterior pressupõe continuidade entre os horários de corte. O anexo da [Resolução ANP nº 884/2022](https://ceaf.mpba.mp.br/wp-content/uploads/2022/10/Resolucao-884-2022-da-ANP-Agencia-Nacional-do-Petroleo-Gas-Natural-e-Biocombustiveis-BR.pdf) descreve a abertura como estoque físico no início do dia e o fechamento como medição física dos tanques. Portanto, eventuais movimentos entre a leitura e o corte precisam ser conciliados; a herança do valor é a regra operacional informada pelo usuário, não uma autorização para ignorar esse intervalo.

Para o fechamento, a tela aceitará lançamento manual ou leitura eletrônica identificada. A origem, o horário e a correspondência entre medidor e tanque devem ser apresentados quando disponíveis. A análise confirmou leituras persistidas em `LEITURASTANQUES` e registros relacionados em `MOVTANQUES` e `ESTOQUETANQUELMC`. Ainda é necessário confirmar a origem específica no TLS, o código do modelo e o significado e unidade dos valores. Há diferenças de datas entre as tabelas que impedem assumir a data operacional somente pela coincidência do volume. Ver o [relatório de medições](medicoes-tanques.md). A necessidade de captura direta depende também da continuidade do processo que fornece novas leituras sem o desktop Xpert.

Leitura ausente, antiga, duplicada ou atribuída a outro tanque deve gerar uma pendência. A indisponibilidade do TLS não preencherá o físico com o saldo escritural; o operador poderá registrar uma medição manual válida. Havendo medição manual e eletrônica para o mesmo fechamento, conferir a divergência e identificar a origem utilizada, sem somar ou fazer média automática.

Uma correção no fechamento de um dia exige recalcular a prévia da abertura e do saldo escritural do dia seguinte e verificar as dependências posteriores. Os fechamentos físicos posteriores permanecem vinculados às respectivas medições. Alterações de notas, datas e turnos também recalculam a conferência, preservando as medições que não foram corrigidas.

Toda gravação utilizará campos existentes. A identificação exata dos campos de abertura, fechamento e origem da medição continua pendente; a informação do usuário esclarece o processo, mas não comprova o significado de `QTDETANQUE` ou `QTDECONCILIACAO`. A conferência permite simulação manual desse fluxo; captura automática validada e gravação de fechamentos continuam pendentes.

## Mudança publicada em 2026

A [Resolução CNPE nº 10/2026, art. 2º, publicada no DOU de 14/08/2026](https://www.gov.br/mme/pt-br/assuntos/conselhos-e-comites/cnpe/resolucoes-do-cnpe/2026/resolucao-10-dou.pdf) estabelece a diretriz de escrituração exclusivamente por meios eletrônicos certificados. Recomenda à ANP atualizar a regulamentação em até 180 dias da publicação e providenciar a padronização e a disponibilização eletrônica dos dados com frequência mínima mensal.

Isso exige que o projeto permita futuras versões de regras e exportações. A resolução não fornece, por si, o contrato técnico de transmissão nem a especificação de certificação do nosso software. Não foi localizada nesta pesquisa uma nova norma operacional da ANP substituindo a 884/2022. Os links do catálogo legislativo da ANP para o texto consolidado não puderam ser abertos; revalidar a situação normativa antes da emissão oficial.

## Verificações propostas para a aplicação

Estas são decisões de implementação propostas; não são uma lista de comandos exigidos pela ANP.

| Verificação | Evidência apresentada ao operador | Tratamento inicial |
| --- | --- | --- |
| Dia sem registro | Calendário, produto e período de operação | Pendência após confirmar atividade do produto |
| Livros repetidos | Filial, produto, data e identificadores | Investigar o histórico e impedir duplicidades usando a estrutura existente |
| Encerrante ausente | Bico e campo ausente | Dado insuficiente |
| Encerrante final menor que inicial | Leituras e histórico de intervenção | Investigar troca ou virada do contador |
| Venda divergente | Valor salvo e cálculo reproduzível | Conferir precisão, aferições e intervenções |
| Recebimento sem documentação correspondente | Documento, produto, tanque e volume | Revisar vínculo e data efetiva da descarga |
| Distribuição de recebimento divergente | Total documental e parcelas por tanque | Conferir rateio e evitar somar a mesma entrada duas vezes |
| Estoque negativo ou acima da capacidade | Medição, tanque e capacidade histórica | Revisar medição/cadastro |
| Descontinuidade entre dias | Fechamento anterior e abertura seguinte | Verificar horário de corte e operações intermediárias |
| Diferença física e escritural | Abertura, entradas, saídas e fechamento | Apurar causa e registrar justificativa com evidências |
| Perda ou sobra acima do limite de alerta | Diferença em litros, percentual, base de cálculo e limite aplicável | Destacar a ocorrência e apresentar conferências e correções sustentadas pelos dados de origem |
| Coincidências repetidas entre físico e escritural | Histórico de diferenças e origem das medições | Sinalizar para conferência; igualdade comprovada é válida e não deve ser alterada para criar variação |
| Alteração em período já conferido | Autor, instante e valores antes/depois | Revalidar os dias afetados |
| Nota de entrada com turno/data operacional divergentes | Cabeçalho, movimentos, parcelas por tanque e turno | Conferir o vínculo e simular a troca antes de aplicar |
| Intervalo incomum entre emissão e entrada | Datas com origem validada, prazo de transporte e evidência de descarga | Localizar notas deslocadas para outros dias/meses, distinguindo erro de data de digitação tardia; não corrigir pela duração provável da viagem |

## Alertas de perdas e sobras e sugestões de correção

Em 06/10/2026, o usuário solicitou aviso quando perdas ou sobras ultrapassarem os parâmetros, sugestão de valor dentro da faixa e atenção à igualdade constante com o estoque escritural. O fluxo proposto para atender à conferência e às correções é:

1. Exibir estoque escritural, estoque físico medido, diferença com sinal, origem dos valores e data do fechamento. Perda e sobra devem permanecer identificáveis, inclusive no detalhamento dos tanques quando houver dados suficientes.
2. Na prévia, exibir explicitamente a fórmula `diferença / estoque escritural × 100`, com referência de 0,6% e indicação de que o critério requer homologação para emissão. A base escritural foi confirmada pelo usuário em 09/10/2026 e substituiu o fechamento físico usado desde 06/10/2026. A tela não declara conformidade fiscal. Dados ausentes mantêm o resultado pendente; escritural zero não recebe percentual ou classificação. A gravação operacional preserva as medições confirmadas e exige justificativa acima da referência, sem declarar a emissão homologada.
3. Destacar variações que ultrapassem o limite. A comparação deve usar a precisão validada, antes do arredondamento de exibição. O parâmetro de 0,6% da Resolução ANP nº 884/2022 é uma referência para alerta e apuração; ultrapassá-lo não autoriza substituir uma medição real por um valor que passe na conferência.
4. Propor a revisão da medição, das notas, da data operacional, do turno, dos encerrantes e das aferições. Um valor corrigido deve decorrer de uma fonte identificada ou de um erro comprovado. Mostrar o registro afetado, a evidência e o efeito calculado antes de aplicar. A correção continua válida mesmo que a diferença real permaneça acima do limite; nesse caso, manter o alerta e a investigação.
5. Apresentar a faixa como referência de análise, sem preencher automaticamente o estoque físico com um ponto dessa faixa. Simulações devem ser identificadas e não podem ser gravadas como se fossem medições. Não gerar valores aleatórios ou pequenas diferenças para aparentar variação natural.
6. Aceitar coincidência entre físico e escritural quando sustentada pelos dados. Repetição no histórico pode ser sinal de conferência da origem das medições, sem constituir prova de erro ou obrigação de produzir uma diferença. O critério dessa sinalização ainda precisa ser definido.
7. Aplicar correções somente nos registros e campos existentes, recalculando os efeitos nos dias relacionados. A interface e o DOT seguirão as mesmas regras de evidência e consistência.

O art. 5º da [Resolução ANP nº 884/2022](https://ceaf.mpba.mp.br/wp-content/uploads/2022/10/Resolucao-884-2022-da-ANP-Agencia-Nacional-do-Petroleo-Gas-Natural-e-Biocombustiveis-BR.pdf) prevê apuração das causas e registro de justificativas para as variações ali descritas. A [orientação da ANP sobre registro de perdas e sobras, pergunta 5](https://www.gov.br/anp/pt-br/acesso-a-informacao/perguntas-frequentes/agente-economico/livro-de-movimentacao-de-combustiveis) também referencia 0,6%. Fontes consultadas novamente em 06/10/2026; permanece pendente a homologação da base percentual na implementação.

Casos de aceitação: perda e sobra acima do limite geram alerta; a classificação no limite respeita a precisão definida; falta de medição ou base inválida não recebe estado de conformidade; igualdade real é aceita; uma correção baseada em documento ou medição mostra sua origem e impacto; ausência de evidência mantém a pendência, sem fabricar um valor substituto.

A conferência diária implementa prévia em litros e percentual e gravação explícita de livros novos ou refeitos. A medição não é preenchida para caber na faixa. A API de balanço mantém a faixa matemática como referência de análise. A base física está explicitada como interpretação operacional; segue pendente sua homologação para emissão. Correções das fontes de leitura, reconstrução automática em cadeia e análise de coincidências históricas ainda não foram implementadas. Veja [gravação e limites](gravacao-lmc.md).

## Pontos pendentes de homologação

- A origem física dos volumes de livros legados em `LMCTANQUES`. A nova gravação usa `QTDETANQUE` para abertura física confirmada e `QTDECONCILIACAO` para fechamento físico confirmado, com auditoria em arquivo local e apenas a observação do operador no campo existente `OBS`; esse contrato não valida retroativamente os volumes antigos.
- A origem manual/eletrônica dos registros de leitura já encontrados, a identificação do código de medidor 11 e a correspondência entre datas de `LEITURASTANQUES`, `MOVTANQUES` e `ESTOQUETANQUELMC`. A persistência de leituras está confirmada; a atribuição específica ao TLS e sua aplicação no fechamento ainda não.
- A homologação para emissão da referência de 0,6%: a aplicação usa o estoque escritural como base, confirmada pelo usuário em 09/10/2026, com fórmula explícita e sem classificar escritural zero. O texto consultado vincula a variação ao estoque físico e ao campo 8, mas não apresenta uma equação completa. A base adotada não homologa o critério nem as fontes para escrituração oficial.
- Tratamento de virada de contador, troca de bicos/tanques e seus vínculos históricos.
- Política de truncamento, casas decimais e precisão dos campos `real`, `float` e `money` do Xpert.
- Preços diferentes no mesmo dia, documentos cancelados, devoluções e outras movimentações.
- Requisitos estaduais aplicáveis, modelo final de impressão/exportação, termos e política de retenção. A localização do computador não determina a UF do estabelecimento.

O sistema e o DOT deverão sinalizar dado ausente e propor investigação. Ajustes de estoque, leituras ou documentos precisam de evidência; uma diferença não deve gerar automaticamente valores artificiais para zerar o balanço.
