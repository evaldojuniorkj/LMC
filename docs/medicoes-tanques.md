# Leituras de tanques no ATXDADOS

## Tratamento de leituras inválidas na interface

Na cópia substituída em 08/10/2026 (filial 4500), foram encontradas leituras de `MOVTANQUES.LEITURA` na ordem de 10¹⁵ a 10²⁹ litros em 31/01 e 01/02/2026. A conversão direta para decimal fazia a consulta inteira do dia falhar. Não foi determinada a origem desses valores.

As três fontes opcionais de medição agora validam a faixa de 0 a 1 bilhão de litros **antes** da conversão, conforme a faixa já aceita pelo formulário. A consulta usa `CASE` dentro de `CONVERT`, compatível com o nível 100 da base. Valor fora da faixa retorna volume indisponível e mantém o valor original textual, fonte, ID e data para conferência. Não é convertido em zero, descartado silenciosamente, nem oferecido para preencher abertura ou fechamento. O editor apresenta aviso e continua permitindo informar uma medição real. Isso não remove impedimentos relativos a notas, bicos ou livros.

Nenhum dado ou estrutura foi alterado para tratar o caso. Verificação HTTP somente leitura: `tests/verificar-leituras-invalidas.ps1`.

Análise realizada em 06/10/2026 na cópia de desenvolvimento, filial `17581`. Foram consultados metadados, configurações selecionadas e registros de tanques, sem modificar dados ou estrutura, executar procedimentos ou conectar a equipamentos.

## Resultado

Existem leituras persistidas no banco, com tanque, data, horário e volume. A configuração de leitura automática está habilitada. Isso permite construir a consulta dessas leituras na nova interface. A atribuição específica ao TLS Gilbarco Veeder-Root ainda não está comprovada: o modelo aparece como código numérico sem descrição identificada. Tampouco foi demonstrado qual processo continuará alimentando o banco quando o desktop Xpert deixar de ser utilizado.

| Tabela | Evidência na cópia | Uso na nova interface |
| --- | --- | --- |
| `LEITURASTANQUES` | 6 registros, tanques 1 a 6, em 29/09/2026 entre 23:00:12 e 23:00:27; campos `QTDELIDA`, `QTDEANTERIOR`, `DATA`, `SINCRONIZADO` | Consulta de leituras disponíveis, preservando tanque e instante |
| `MOVTANQUES` | 1.144 registros de 06/04/2024 a 29/09/2026; campos `LEITURA`, `QTDESISTEMA`, `DTACONTA`, `ID_TURNOS`, `CENTIMETROS`, `ABERTURA` | Histórico candidato para conferência; interpretar datas, zeros e turno antes de usar no fechamento |
| `ESTOQUETANQUELMC` | 915 registros de 04/08/2024 a 28/09/2026; `QTDETANQUE` preenchido e `QTDECONCILIACAO` nulo em todos | Comparação com os registros usados no processo de LMC, após resolver a correspondência de datas |
| `LMCTANQUES` | Valores ligados ao livro por `ID_FILIAL` e `ID_LMC` | Exibir o que já está escriturado e comparar com as fontes de leitura |
| `CFG_MEDIDORTANQUE` | Uma configuração na filial; `MODELO_MEDIDOR = 11`, endereços de comunicação preenchidos | Evidência de configuração de medidor; código 11 ainda sem identificação de fabricante/modelo |
| `CFGGERAL` | `AUTOLEITURATANQUES = 1` | Evidência de que a opção de leitura automática está habilitada |

Os intervalos são os presentes na cópia, sem comprovar continuidade diária ou atualização do ambiente de produção. Em `LEITURASTANQUES`, há apenas um registro por tanque nesta cópia; a hipótese de tabela de última leitura ou fila é plausível, mas o comportamento de atualização não foi observado.

Os registros dessa tabela têm `SINCRONIZADO = 0`. O significado do código permanece pendente; não deve ser convertido em rótulo de sincronização bem-sucedida. `QTDEANTERIOR` apresenta valores negativos em cinco dos seis registros e não foi validado como leitura física anterior. As tabelas examinadas não possuem um campo explícito que identifique a origem como manual ou TLS.

## Outras integrações e protocolos

O usuário informou que existem outros protocolos ativos. O projeto deve contemplar diferentes integrações de medição e automação usadas pelos clientes, sem assumir que todos utilizam TLS ou um único fabricante. O levantamento desta cópia não determina a configuração dos demais clientes.

A inspeção ampliada encontrou duas configurações distintas na filial 17581:

| Função | Configuração observada | Evidência de uso nos dados |
| --- | --- | --- |
| Medição de tanques | `CFG_MEDIDORTANQUE.MODELO_MEDIDOR = 11`; `CFGGERAL.AUTOLEITURATANQUES = 1` | Seis leituras de 29/09/2026 em `LEITURASTANQUES` |
| Automação de bombas/bicos | `CFGCONSOLE.MODELOAUTOMACAO = 39`; `CFGGERAL.USARAUTOMACAO = 1` | 46 registros em `CONSOLE` com `AUTOMACAO = 1`, em 06/10/2026, associados a 13 bicos |

`CONSOLE` possui também 41 registros com `AUTOMACAO = 0`. `CONSOLEARQUIVO` contém 630.258 registros entre 26/03/2024 e 06/10/2026, associados a 22 bicos no histórico. Essas tabelas possuem quantidade abastecida e encerrantes; seus registros não substituem uma medição física de tanque.

Os códigos 11 e 39 continuam sem correspondência validada com nomes de protocolos ou fabricantes. Configuração preenchida e registros históricos são evidências diferentes de uma conexão ativa no momento: nenhuma comunicação com dispositivos foi testada. Tampouco há evidência suficiente de que vários protocolos de medição operem simultaneamente nesta filial. Campos de comunicação adicionais, como `IPS2`, não comprovam um protocolo ativo só por estarem preenchidos.

Na implementação, cada integração deverá identificar sua função, fabricante/modelo quando conhecido, origem dos valores, unidade, instante de captura e associação a filial/tanque/bico. O acesso inicial aos dados existentes pode ser comum; conectores diretos, se necessários, deverão ser específicos para os protocolos identificados. Isso será tratado na aplicação usando a estrutura disponível, sem criar campos ou tabelas.

## Correspondência entre leituras e datas

Os seis valores de `LEITURASTANQUES.QTDELIDA` de 29/09 coincidem, a duas casas decimais, com `MOVTANQUES.LEITURA` de 29/09 e `ESTOQUETANQUELMC.QTDETANQUE` de **28/09**:

| Tanque | Horário em LEITURASTANQUES, dia 29/09 | Volume registrado | Data em MOVTANQUES | Data em ESTOQUETANQUELMC |
| --- | --- | --- | --- | --- |
| 1 | 23:00:12 | 10.238,95 | 29/09/2026 | 28/09/2026 |
| 2 | 23:00:16 | 12.504,73 | 29/09/2026 | 28/09/2026 |
| 3 | 23:00:19 | 6.332,32 | 29/09/2026 | 28/09/2026 |
| 4 | 23:00:22 | 5.219,81 | 29/09/2026 | 28/09/2026 |
| 5 | 23:00:24 | 7.431,30 | 29/09/2026 | 28/09/2026 |
| 6 | 23:00:27 | 1.121,51 | 29/09/2026 | 28/09/2026 |

A comparação numérica utiliza tolerância exploratória de 0,02 para diferenças de representação entre `real` e `float`. Ela não representa tolerância de perdas nem validação metrológica.

Em setembro/2026, dos 42 pares disponíveis entre estoque de um dia e movimento do dia seguinte, 41 têm valores coincidentes dentro dessa tolerância. Ao comparar as mesmas datas, nenhum dos 42 pares coincide. No histórico completo, apenas 140 de 273 pares com deslocamento de um dia coincidem. Portanto, **não implementar uma subtração automática de um dia**: é uma associação observada, sem causa confirmada e com exceções.

Uma exceção identificada é o tanque 2: `ESTOQUETANQUELMC` de 22/09 registra 12.450,68, enquanto `MOVTANQUES.LEITURA` de 23/09 registra 12.463,89. Não foi identificada a causa dessa diferença. Não foram encontrados grupos repetidos por filial, tanque e data nas três tabelas de leituras comparadas; as contagens não decorrem de multiplicação por duplicidade de join.

## Relação com os livros existentes

Foram comparadas as 180 linhas de tanques dos LMCs entre 30/08/2026 e 28/09/2026:

- Nas 180, `LMCTANQUES.QTDETANQUE` coincide com `QTDECONCILIACAO` do dia anterior. Isso sustenta a hipótese de abertura herdada, mas não comprova que o fechamento herdado represente medição física.
- Em 152, `QTDECONCILIACAO` coincide com `QTDETANQUE + recebimentos de LMCCOMPROVANTES − vendas de LMCBICOS`. As demais 28 precisam de análise das outras movimentações e regras; não foram classificadas automaticamente como erros.
- Há registros de `ESTOQUETANQUELMC` da mesma data para 156 das 180 linhas. Em nenhuma dessas 156, o volume coincide com `QTDETANQUE` ou `QTDECONCILIACAO` do livro.
- Há 4 valores negativos em `QTDETANQUE` e 8 em `QTDECONCILIACAO`. São achados para revisão dos dados e de sua interpretação, sem conclusão sobre estoque físico real ou conformidade fiscal.

Exemplo: no tanque 1, o LMC de 28/09 tem `QTDETANQUE = -543,18` e `QTDECONCILIACAO = -3.449,76`, enquanto `ESTOQUETANQUELMC` da mesma data tem 10.238,95, valor também encontrado na leitura de 29/09. A nova interface deverá mostrar as fontes separadamente até validar seu significado e corte temporal.

## Turnos, lacunas e histórico de alterações

Dos 1.144 registros de `MOVTANQUES`, 992 têm `LEITURA = 0` e 152 têm leitura não zero; não há leitura nula. Em setembro/2026, existem 48 registros distribuídos por oito dias, todos com leitura não zero. Não tratar zero como prova automática de tanque vazio nem afirmar que o histórico cobre todos os dias.

Todos os registros de `MOVTANQUES` apresentam `ABERTURA = 0`; nenhum apresenta centímetros positivos. Isso não distingue de forma suficiente lançamento manual de leitura eletrônica.

Há 144 movimentos de 2026 ligados ao turno `ID_TURNOS = 1`, cuja `DATATURNO` é 25/03/2024. Os demais movimentos têm correspondência de data com o turno. O vínculo existe, mas esses 144 casos exigem interpretar o uso do turno antes de filtrar medições pela data do cadastro de turnos ou modificar seus vínculos. Não foram corrigidos.

`EDICAO_LEITURA_TANQUES` já existe, com leitura anterior, leitura atual, data/hora, usuário e referência a `MOVTANQUES`, porém está vazia. `MOVQTDETANQUES` possui 51 registros de alterações de quantidade. São recursos existentes a avaliar para rastreabilidade, sem necessidade de propor novas tabelas neste levantamento. Antes de qualquer gravação, mapear as rotinas, chaves e efeitos: por exemplo, a chave de `MOVTANQUES` inclui `ID_DB`, enquanto `EDICAO_LEITURA_TANQUES` não possui esse campo.

As 47 procedures e triggers cujos nomes contêm os termos de tanques/medidores pesquisados estão criptografadas. Os nomes e parâmetros permitem identificar operações candidatas, mas não demonstram qual processo captura o equipamento nem a origem de cada valor. Nenhuma foi executada.

## Consequências para a implementação

1. Usar as leituras existentes para construir a tela de consulta e conferência, mantendo o valor e a data de cada fonte visíveis.
2. Validar a associação entre instante da leitura, data operacional e campos do LMC antes de alimentar automaticamente o fechamento. Não equiparar `QTDESISTEMA`, `QTDEANTERIOR` ou `QTDECONCILIACAO` à medição física pelo nome.
3. Identificar o código de modelo 11 e o processo que alimenta `LEITURASTANQUES`. A necessidade de captura direta do TLS depende de esse processo permanecer disponível sem o desktop Xpert; os registros históricos não resolvem essa questão.
4. Preservar as tabelas, campos e tipos existentes. A eventual manutenção de medições e seu histórico usará os recursos já disponíveis, depois de mapeados.

## Reprodução

O [script de diagnóstico](../sql/04-origem-medicoes-tanques.sql) foi executado com sucesso. Ele contém somente consultas, variáveis locais, CTEs e configurações de sessão; não cria objetos nem executa rotinas de negócio. A filial está explícita e o intervalo de comparação de LMC é calculado a partir da última data existente, sem depender da data atual.

```powershell
sqlcmd -S localhost -d ATXDADOS -E -C -b -l 5 -t 30 -W -s '|' -i sql/04-origem-medicoes-tanques.sql
```
