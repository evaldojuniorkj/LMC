# Captura dos encerrantes de fechamento

Proposta para a nova interface, baseada em consultas realizadas em 06/10/2026 na filial 17581. O usuário confirmou que a automação existente atualiza os encerrantes quando as vendas são registradas. A consulta, conferência e geração pela nova interface ainda não foram implementadas. Nenhum dado ou objeto do banco foi alterado.

## Fontes encontradas

Os encerrantes pertencem aos bicos e à automação de abastecimento. As leituras de tanques examinadas no cenário TLS fornecem outra informação: estoque físico. As fontes precisam ser distinguidas na aplicação.

| Fonte | Conteúdo encontrado | Uso proposto |
| --- | --- | --- |
| `CONSOLEARQUIVO` | 630.258 registros de abastecimentos, com `DATA`, `DATABAIXA`, `ENCERRANTEANTERIOR`, `ENCERRANTE`, quantidade e estados | Principal fonte candidata para a sequência dos abastecimentos e o encerrante no corte do período; 540 fechamentos coincidentes no recorte |
| `ENCERRANTESTURNOS` | 21.312 registros por filial, bico e turno; abertura, fechamento, aferição, pendências e preço | Conferência por turno, considerando que seus valores podem alcançar abastecimentos após a meia-noite |
| `TURNOS` | Data operacional, número, situação e data de fechamento | Identificar o período de cada leitura; o significado dos estados ainda requer validação |
| `ENCERRANTES` | 20.746 registros diários até 05/10/2026 | Fonte de comparação; não assumir equivalência com o LMC |
| `CONSOLE` | Abastecimentos com encerrantes, datas e estados | Fonte complementar a conciliar com o arquivo; não somar as duas tabelas sem mapear o arquivamento e possíveis sobreposições |
| `BICOS` | `LEITURAATUAL` e `ULTIMALEITURABOMBA` | Contexto da leitura atual; não usar como fechamento de um dia histórico |
| `LMCBICOS` | Encerrantes, aferições e vendas associados ao livro | Destino dos valores validados para novos livros e referência dos livros existentes |

Os nomes das tabelas não demonstram qual programa ou protocolo alimentou cada valor. A automação configurada tem código de modelo 39, ainda sem correspondência validada com fabricante ou protocolo.

## Comparação com o LMC existente

Entre 30/08/2026 e 28/09/2026, foram comparadas 540 linhas de bicos dos LMCs. Cada bico/data desse recorte possui um único registro em `ENCERRANTESTURNOS`, identificado por `TURNOS.DATATURNO`:

- 538 encerrantes de fechamento e 538 de abertura coincidem com os valores por turno.
- As 540 aferições coincidem com os registros por turno.
- Apenas 6 fechamentos e 6 aberturas coincidem com a tabela diária `ENCERRANTES`.

As comparações usam tolerância exploratória de 0,02 L para tipos numéricos diferentes; não é tolerância normativa. Essa primeira comparação identificou a tabela por turno como candidata. A investigação posterior de `CONSOLEARQUIVO`, descrita abaixo, encontrou correspondência maior e passou a orientar a seleção do encerrante por período. As coincidências não comprovam qual rotina gerou os livros nem autorizam substituir livros existentes.

As duas diferenças de fechamento estão em 04/09/2026, LMC 4344, turno 8933:

| Bico | Fechamento no LMC | Fechamento por turno | LMC menos turno |
| --- | --- | --- | --- |
| 11 | 182.727,27 | 182.744,44 | −17,17 |
| 15 | 233.130,76 | 233.143,97 | −13,21 |

Na tabela diária, os 24 fechamentos de 05/10/2026 estão zerados, enquanto há encerrantes preenchidos no turno dessa data. No turno de 06/10, os fechamentos estão nulos. Os 30 turnos do período comparado têm `DATAFECHAMENTO` nula, `DEFINITIVO = 0` e `STATUSTURNO = 0`, apesar dos encerrantes preenchidos. Esses campos não podem determinar sozinhos que um fechamento está concluído ou pendente antes da homologação de seus significados.

`CFGLMC` informa horário 23:59, `CAPTURATURNO = 0`, `CAPTURAENCERRANTES = 0` e `CALCENCBOMBA = 1`. Essa configuração atual não demonstra como os dados históricos foram produzidos. O horário de corte e o modo efetivo serão validados para cada posto; não fixar 23:59 para todos.

## Validação de CONSOLEARQUIVO

Após a indicação do usuário, a investigação comparou a sequência dos abastecimentos com os mesmos 540 registros de LMC. No recorte de 30/08 a 28/09/2026, todos os bicos/dias do LMC têm abastecimentos no arquivo. Usando `DATA` no intervalo de 00:00 do dia até, sem incluir, 00:00 do dia seguinte, o `ENCERRANTE` do último registro por bico coincide com **540 dos 540 fechamentos**. O encerrante é escolhido pela sequência temporal, não pelo maior valor numérico do contador.

Esse é o corte que reproduziu a amostra. O campo `CFGLMC.HORACAPTURA = 23:59` não será interpretado automaticamente como exclusão de todo o último minuto do dia, nem como regra fixa para todos os postos. Antes da gravação, a data operacional e o corte aplicável precisam estar definidos.

Ao agrupar os mesmos dados por `DATABAIXA`, mantendo a ordenação do evento por `DATA`, a correspondência cai para 537 de 540 fechamentos. Há oito registros no recorte com datas distintas entre `DATA` e `DATABAIXA`. A interface deve conservar a distinção entre essas datas; uma baixa posterior não deve mover automaticamente o abastecimento para outro dia.

As duas divergências por turno de 04/09 coincidem com registros do dia seguinte:

| Bico | Registro do arquivo | DATA | ENCERRANTEANTERIOR | ENCERRANTE |
| --- | --- | --- | --- | --- |
| 11 | 626873 | 05/09/2026 00:04:56 | 182.727,27 | 182.744,44 |
| 15 | 626874 | 05/09/2026 00:08:24 | 233.130,76 | 233.143,97 |

O valor anterior desses abastecimentos coincide com o fechamento do LMC de 04/09; o valor final coincide com o fechamento do turno atribuído a 04/09. Isso sustenta a hipótese de diferença no corte do turno e do dia, sem comprovar qual rotina transferiu os valores.

Há quatro bicos/dias com mais de um registro no último horário. Em todos, os encerrantes anterior e final são iguais, mas `STATUS` e `ABASTECIMENTO_GERADO` diferem. Não houve ambiguidade numérica do fechamento nesses quatro casos. A consulta usa o ID para tornar a listagem determinística; em produção, empate com valores diferentes exigirá análise, não escolha arbitrária por ID. Tampouco se deve somar esses registros como vendas independentes sem interpretar os estados.

O `ENCERRANTEANTERIOR` do primeiro abastecimento coincide com a abertura de 538 dos 540 registros. As exceções são o bico 13 nos dias 07/09 e 18/09, com diferença de 0,03 L em relação à abertura do LMC. A abertura continuará sendo conferida contra o fechamento anterior e os eventos de intervenção. A soma simples de `QTDE` coincide com as vendas do LMC em apenas 353 linhas; aferições, precisão e possíveis duplicidades/estados precisam ser considerados. Não substituir o cálculo do livro pela soma indiscriminada do arquivo.

## Fluxo proposto

1. Selecionar filial e data operacional, identificar os bicos e os vínculos com tanques/produtos válidos no período e localizar os turnos correspondentes.
2. Consultar a sequência em `CONSOLEARQUIVO` por filial, bico e intervalo operacional e identificar o último encerrante válido dentro do corte. Conferir esse resultado com `ENCERRANTESTURNOS` e explicitar diferenças quando um turno atravessar o limite do dia. Validar a abertura contra o fechamento anterior e o primeiro evento. Não somar encerrantes acumulados nem escolher o maior contador como fechamento. A política dos estados dos registros ainda precisa ser homologada.
3. Conferir a continuidade entre turnos e dias, as aferições, a sequência de abastecimentos, leituras ausentes, duplicidades, intervenções e eventuais viradas ou trocas de contador. Um bico sem vendas pode ter abertura igual ao fechamento; ausência de vendas não comprova ausência de movimentos ou de aferição.
4. Exibir por bico a origem, a data/turno, abertura, fechamento, aferição e vendas calculadas, destacando divergências e dados insuficientes. A data de replicação não será apresentada como instante de medição sem comprovação.
5. Preparar o LMC com os valores validados e gravá-lo nas tabelas existentes mediante a operação de negócio definida, respeitando dependências e triggers. Se a origem mudar depois da prévia, invalidar a prévia e conferir novamente. Nenhuma lacuna deve ser preenchida automaticamente com um valor calculado a partir das perdas desejadas.

## Alimentação pela automação existente

O usuário esclareceu que a automação já faz a atualização dos encerrantes ao registrar as vendas. O projeto adotará essa alimentação existente. A nova interface consultará os registros do banco e os usará para preparar o LMC; implementar outro coletor ou conectar diretamente aos equipamentos não é requisito desta etapa.

O fluxo definido é: venda registrada → automação atualiza os encerrantes → aplicação consulta os valores do período → conferência por bico → preparação e gravação do LMC nas tabelas existentes. A captura realizada pela automação não dependerá de manter a nova tela aberta.

A comparação de `CONSOLEARQUIVO` validou a correspondência numérica dos fechamentos e evidenciou a diferença entre corte diário e registro por turno nesta amostra. A implementação ainda precisa homologar os estados, a cobertura dos movimentos, dias sem abastecimento e o arquivamento de `CONSOLE`. A leitura selecionada deve pertencer ao período correto, considerando abastecimentos em curso, eventos recebidos com atraso e intervenções nos contadores. Uma venda do dia seguinte não pode alterar silenciosamente a prévia de fechamento do dia anterior.

Quando houver encerrantes pendentes ou divergentes, a interface mostrará o motivo para conferência. Em períodos sem movimentos, abertura e fechamento podem coincidir, depois de verificar continuidade e aferições. A aplicação não substituirá uma leitura ausente por zero nem deduzirá um fechamento apenas da ausência de vendas.

As gravações da nova interface serão limitadas às operações de LMC e correções mapeadas nas tabelas existentes. Os protocolos da automação permanecem como contexto da origem dos dados; identificar seus nomes não é pré-requisito para a consulta do banco. A revisão do coletor somente será necessária se surgir uma falha concreta de alimentação que exija esse trabalho.

## Reprodução

O [diagnóstico de encerrantes](../sql/06-fontes-encerrantes.sql) foi executado com sucesso. Ele compara somente grupos únicos por bico/data, apresenta divergências e não modifica dados. O recorte com apenas um registro por bico/dia não valida sozinho o comportamento para postos com vários turnos.

O [diagnóstico de CONSOLEARQUIVO](../sql/07-consolearquivo-encerrantes.sql) também foi executado com sucesso. Compara os fechamentos, as duas datas, os empates e as aberturas e procura valores correspondentes às divergências de turno no dia seguinte. Ambos são somente leitura.

```powershell
sqlcmd -S localhost -d ATXDADOS -E -C -b -l 5 -t 30 -W -s '|' -i sql/06-fontes-encerrantes.sql
sqlcmd -S localhost -d ATXDADOS -E -C -b -l 5 -t 30 -W -s '|' -i sql/07-consolearquivo-encerrantes.sql
```
