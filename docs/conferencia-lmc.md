# Conferência diária do LMC

Implementada em 06/10/2026, após a revisão de notas. A tela agora oferece uma grade mensal e um editor compacto por dia e combustível, com prévia do balanço e gravação de livros novos ou refeitos. A prévia não escreve; a ação explícita de salvar grava os dados conferidos. A correção de notas permanece disponível no formulário. Veja a [transação de gravação, histórico e limites](gravacao-lmc.md).

## Dados exibidos

- Livros e vínculos históricos: `LMC`, `LMCTANQUES`, `LMCBICOS`. Um tanque encontrado apenas no cadastro atual é identificado como pendência; não se presume que seu vínculo histórico seja o atual.
- Entradas atuais: parcelas de `LMCENTRADATANQUES`, ligadas à chave completa de `COMPROVANTES` (filial, origem e documento). `DATA` é emissão, `DTAEMISSAO` é entrada e `DTACONTA` governa o dia operacional. O turno geral é aceito em qualquer data; turno diário deve corresponder à data. Parcelas não são multiplicadas por movimentos. Canceladas são mostradas e excluídas; documentos ausentes, datas, turnos, volumes e movimentos incoerentes impedem o cálculo.
- Recebimentos já gravados: `LMCCOMPROVANTES`, separados das entradas atuais. A comparação por tanque, número e volume serve para triagem; não comprova identidade documental, pois falta a chave completa no livro. Corrigir a nota não altera automaticamente esses recebimentos.
- Automação: primeiro `ENCERRANTEANTERIOR` e último `ENCERRANTE` de `CONSOLEARQUIVO`, por bico, no intervalo `[00h do dia, 00h seguinte)`, usando `DATA`. Empates com valores distintos ou ausentes são ambíguos. Não se filtram códigos de estado ainda desconhecidos, nem se soma `QTDE` como venda adicional.
- Vendas e aferições: valores salvos em `LMCBICOS`, conferidos contra abertura, fechamento e aferição, e contra os dois extremos da automação. Diferença de até 0,02 L é somente a tolerância de representação explorada para campos `real`/`float`, não um limite de perda ou sobra. A precisão e o truncamento para emissão oficial continuam pendentes.
- Leituras próximas: `MOVTANQUES` (fechamento), `ESTOQUETANQUELMC` e `LEITURASTANQUES`, do dia anterior ao posterior, com data e fonte originais. Nenhuma delas é automaticamente atribuída ao TLS, deslocada de data ou adotada como físico. O operador pode escolher uma leitura para simular e vê aviso quando a data da fonte difere do fechamento selecionado.

O dia 28/09/2026 retorna quatro livros, seis tanques e dezoito bicos. Os encerrantes da automação conferem na amostra. Há valores negativos em `LMCTANQUES` para alguns produtos; são alertados e não viram medições. Em setembro, valores de `ESTOQUETANQUELMC` frequentemente coincidem com `MOVTANQUES` do dia seguinte, mas há exceções. Isso impede automatizar essa mudança de data por simples coincidência numérica.

## Simulação do balanço

A abertura deve ser o fechamento físico do dia anterior. Ela é herdada quando o livro anterior foi gravado pela interface com medição confirmada e ainda coincide com o banco. Para os livros antigos, o operador confere e informa a primeira abertura; não se presume que um saldo legado seja físico. O fechamento atual é informado ou escolhido entre as leituras disponíveis. Ao reabrir um livro salvo pela interface, as medições são reapresentadas se ainda correspondem aos registros. Entradas e encerrantes são consultados novamente no servidor antes da gravação.

Fórmulas da prévia:

- Escritural = soma dos fechamentos físicos anteriores + entradas atuais − vendas conferidas.
- Diferença = soma dos fechamentos físicos atuais − escritural. Negativa indica perda; positiva indica sobra.
- Percentual de referência = diferença ÷ estoque escritural × 100.
- Alerta quando `abs(diferença) > escritural × 0,006`, comparando antes de arredondar para apresentação.
- Para escritural positivo, a faixa matemática é `[escritural × 0,994; escritural × 1,006]`. É informativa; não preenche campos nem oferece um substituto artificial para a medição.

A base do percentual é o estoque escritural, conforme confirmado pelo usuário em 09/10/2026; até essa data a aplicação dividia pelo fechamento físico. A variação vem do campo 8 (perdas + ganhos) do art. 5º, §§ 1º e 2º, da [Resolução ANP 884/2022](https://ceaf.mpba.mp.br/wp-content/uploads/2022/10/Resolucao-884-2022-da-ANP-Agencia-Nacional-do-Petroleo-Gas-Natural-e-Biocombustiveis-BR.pdf). A referência gera alerta e exige justificativa para gravar variações superiores; sua aplicação à emissão precisa de homologação. “Dentro da referência” não declara conformidade fiscal nem valida a origem dos valores.

Medição ausente mantém o cálculo pendente. Escritural zero permite mostrar litros quando disponíveis, mas não recebe percentual ou classificação; havendo diferença, a gravação exige justificativa. Físico zero com escritural positivo é perda de 100% e fica acima da referência. Valores negativos de medição são rejeitados; capacidade excedida e saldo escritural negativo são alertados. Igualdade real é aceita, sem gerar variações. Aferição ausente, livro duplicado, bicos sem arquivo, encerrantes divergentes, virada de contador ou vínculo desconhecido bloqueiam o balanço.

Os rascunhos permanecem apenas na memória do formulário. Atualizar ou trocar dia/combustível descarta valores ainda não salvos. Cada resposta assíncrona pertence à consulta atual; editar uma medição invalida imediatamente a prévia anterior. Quando uma gravação fica sem confirmação por falha de comunicação, a tela mantém o mesmo identificador e conteúdo para uma repetição segura. A conferência de notas conserva seu próprio fluxo transacional.

## API e verificação

- `GET /api/lmc/day?branch=17581&date=2026-09-28`: consulta consistente em transação de leitura, retorna livros, produtos, alertas, tanques, bicos, arquivo, entradas e leituras.
- `POST /api/lmc/balance`: recebe filial, data, produto e um par `previousClosing`/`closing` por tanque; consulta novamente as fontes e devolve somente a prévia. Nenhuma instrução de gravação é executada.

Verificações automatizadas:

```powershell
# Pare a instância local antes de recompilar no Windows.
dotnet run --project tests/Lmc.Checks/Lmc.Checks.csproj
node tests/verificar-interface.mjs
# Com o serviço em execução:
.\tests\verificar-lmc.ps1
```

Os testes do LMC cobrem limites simétricos de perda e sobra, zero, nulos, capacidade, duplicidades, divergências, aferições, cancelamentos, entradas atuais e fontes não identificadas. Os testes HTTP usam a última data disponível da filial e só consultam ou simulam. Os testes antigos de correção têm alguns valores fixos da cópia original; correções feitas pelo usuário podem mudar essas expectativas. Nunca desfazer uma correção do usuário para satisfazer um teste.

## Gravação e próximas etapas

A inserção e a reconstrução já usam transação, versão das fontes, histórico e repetição segura, conforme [gravação do LMC](gravacao-lmc.md). Aferições e dias sem venda são aceitos apenas com evidência suficiente nos registros. Viradas de contador, preços variáveis e fontes ambíguas bloqueiam a operação. Permanecem pendentes a captura automática das medições com corte validado, reconstrução em lote, impressão/emissão e encerramento global da fila de pendências. Todas as operações usarão exclusivamente as estruturas existentes.
