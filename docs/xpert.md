# Mapeamento do banco herdado do Xpert

Levantamento inicial obtido por consultas em 06/10/2026. Posteriormente foram implementadas correções de notas e gravação de LMC; seus ensaios de escrita foram executados com rollback. Os objetos do banco foram preservados.

Decisão do usuário, na mesma data: construir uma nova interface de interação utilizando somente o banco existente, sem depender do aplicativo desktop Xpert. Este levantamento descreve os dados e os recursos do banco que sustentam as operações da interface. O eventual aproveitamento de rotinas existentes depende da validação de seus efeitos.

## Ambiente observado

- Servidor: `AORUS-KZ`, SQL Server versão `15.0.2000.5`.
- Nível de compatibilidade do `ATXDADOS`: 100; manter esse nível e adaptar as consultas.
- Banco: `ATXDADOS`, online, 812 tabelas de usuário.
- A tabela `LMC` contém 3.069 registros, uma filial (`17581`) e quatro produtos, de 26/03/2024 até 28/09/2026.
- Esses números descrevem a cópia disponível. A data mais recente não comprova atraso na produção.
- A autenticação Windows funcionou; a sessão possui `sysadmin` e permissão de leitura de definições.

## Dados e relacionamentos

| Tabela | Campos observados | Uso candidato |
| --- | --- | --- |
| `LMC` | `ID_LMC`, `ID_FILIAL`, `ID_PRODUTOS`, `DTACONTA`, `NROCONTROLE`, `OBS`, valores de vendas | Identificação do livro, data, produto e observações |
| `LMCBICOS` | `ID_LMC`, `ID_FILIAL`, `ID_BICOS`, `ID_TANQUES`, encerrantes, `AFERICAO`, `PPL`, `VENDAS` | Leituras e vendas associadas ao livro |
| `LMCTANQUES` | `ID_LMC`, `ID_FILIAL`, `ID_TANQUES`, `QTDETANQUE`, `QTDECONCILIACAO` | Na nova interface: abertura e fechamento físicos confirmados; a origem física dos volumes legados permanece a conferir |
| `LMCCOMPROVANTES` | `ID_LMC`, `ID_FILIAL`, `ID_TANQUES`, `NROCOMPROVANTE`, `VOLUMERECEBIDO`, `DTAENTRADA`, `CNPJ_FORNECEDOR` | Documentos vinculados ao livro |
| `LMCENTRADATANQUES` | `ID_FILIAL`, `ID_DB`, `ID_COMPROVANTE`, `ID_PRODUTOS`, `ID_TANQUES`, `DTACONTA`, `QTDE` | Entradas ligadas a documentos e tanques |
| `ENCERRANTES` | Filial, bico, data, leituras, aferição e preço | Fonte de comparação com o livro |
| `CONSOLEARQUIVO` | Filial, bico, `DATA`, `DATABAIXA`, `ENCERRANTEANTERIOR`, `ENCERRANTE`, quantidade e estados | Principal candidata para o encerrante no corte: 540 de 540 fechamentos coincidentes no recorte usando `DATA` |
| `ENCERRANTESTURNOS` | Filial, bico, turno, encerrantes de abertura/fechamento, aferição, pendências e preço | Conferência por turno; 538 de 540 fechamentos coincidentes no recorte |
| `ESTOQUETANQUELMC` | `ID_ESTOQUETANQUESLMC`, `ID_FILIAL`, `ID_TANQUES`, `DTACONTA`, `QTDETANQUE`, `QTDECONCILIACAO` | Fonte candidata de medição; quantidades do tipo `real`, significado e origem a confirmar |
| `LEITURASTANQUES` | Filial, tanque, `DATA`, `QTDELIDA`, `QTDEANTERIOR`, `SINCRONIZADO` | Leituras persistidas; origem específica e significado do estado a confirmar |
| `CFG_MEDIDORTANQUE` | Filial, `MODELO_MEDIDOR`, configuração de comunicação | Configuração existente de medidor, código de modelo ainda sem descrição |
| `EDICAO_LEITURA_TANQUES` | Filial, movimento, leituras anterior/atual, data/hora e usuário | Histórico de edição já previsto na estrutura, vazio nesta cópia |
| `BICOS`, `TANQUES`, `PRODUTOS` | Cadastros, vínculos e capacidades | Identificação e conferência cadastral |
| `CFGLMC` | Captura, abertura, cálculo e casas decimais | Configuração existente; validar o significado e a aplicação de cada campo |
| `COMPROVANTES` | `ID_COMPROVANTE`, `ID_FILIAL`, `ID_DB`, `ID_TURNOS`, `DATA`, `DTACONTA`, `DTAEMISSAO` | Nas notas de entrada: `DATA` = emissão; `DTAEMISSAO` = entrada (confirmados pelo usuário); `DTACONTA` = data operacional do lançamento para o LMC |
| `MOVPRODUTOS` | `ID_MOVPRODUTOS`, `ID_FILIAL`, `ID_DB`, `ID_COMPROVANTE`, `ID_TURNOS`, `DATA`, `DTACONTA` | Movimentos associados ao documento e ao turno |
| `TURNOS` | `ID_TURNOS`, `ID_FILIAL`, `DATATURNO`, `TURNO`, `STATUSTURNO`, `DEFINITIVO`, `DATAFECHAMENTO` | Identificação, data e situação do turno; códigos de estado ainda não interpretados |
| `MOVTANQUES` | `ID_MOVTANQUES`, `ID_DB`, `ID_TURNOS`, `ID_FILIAL`, `ID_TANQUES`, `DTACONTA`, `LEITURA`, `QTDESISTEMA`, `ABERTURA`, `CENTIMETROS` | Leituras e quantidade do sistema por tanque/data; vínculo com uma nota específica ainda não demonstrado |

Os vínculos entre o livro e seus detalhes usam **`ID_LMC` e `ID_FILIAL`**. Os vínculos cadastrais também incluem filial. Documentos das entradas usam **`ID_COMPROVANTE`, `ID_FILIAL` e `ID_DB`**. Essas chaves compostas foram confirmadas nas foreign keys; não fazer joins somente pelo identificador numérico.

`LMCENTRADATANQUES` não possui `ID_LMC`. Não somar seus volumes aos de `LMCCOMPROVANTES` como se fossem recebimentos independentes. Primeiro é necessário conciliar os dois níveis de informação.

O índice de `LMC` por produto/filial não é exclusivo por data. A gravação implementada impede duplicidades por transação serializável, trava de aplicação, versão das fontes e identificador da operação, sem criar índices ou constraints. As revisões usam o campo existente `LMC.OBS`, com os valores anteriores. Veja [gravação de LMC](gravacao-lmc.md).

## Rotinas existentes

Foram encontradas `SX_GERALMC`, `SX_LMCAUTOMATICO`, `SX_ESTOQUEABERTURALMC`, `SX_TANQUESLMC`, `SX_BICOSLMC` e outras rotinas relacionadas, além de triggers nas tabelas de livros e detalhes.

Nas rotinas principais e triggers verificadas, `IsEncrypted = 1` e a definição retorna `NULL`, mesmo com permissão de leitura. Conhecemos nomes e parâmetros, mas não seu código ou efeitos. Nenhuma dessas rotinas foi executada. A descoberta não demonstra a existência de uma API pública ou suportada pelo fornecedor.

As rotinas `SX_*` não são chamadas pela implementação. As triggers existentes disparam na escrita direta e permanecem habilitadas. Inserção e reconstrução foram ensaiadas com rollback, comparando livros, fontes físicas, notas, parcelas e configurações antes/depois. A gravação também verifica os dados protegidos durante a própria transação e desfaz efeitos inesperados. Não foram criados ou modificados objetos do banco.

## Configuração observada

Há uma linha em `CFGLMC`. Entre os valores: captura `23:59`, `CAPTURATURNO=0`, `USARULTIMOLMC=1`, `CALCENCBOMBA=1`, `NROCASAS=2`, `ABERTURA=0` e `NAO_SALVAR_LMCS_PROD_MESMO_DIA=1`. Os nomes sugerem comportamentos, mas não constituem documentação suficiente para reproduzi-los. A relação da configuração com filial também requer validação.

Os campos volumétricos usam uma combinação de `real`, `float` e `money`. O serviço deverá preservar os valores originais e adotar cálculos decimais e tolerâncias documentadas, evitando falsos alertas de ponto flutuante.

## Verificação dos livros existentes

Os scripts de inventário e diagnóstico foram executados com sucesso. Para a filial 17581, o período de 30/08/2026 a 28/09/2026 contém 120 livros, 540 linhas de bicos e 180 linhas de tanques.

- Não apareceram duplicidades por produto/data, livros sem bicos ou tanques, encerrantes ausentes ou decrescentes nesse período.
- Não há livros sem data na filial consultada.
- As 540 linhas de bicos são compatíveis com `VENDAS = ENCERRANTEFECHAMENTO - ENCERRANTEABERTURA - AFERICAO`, usando as leituras com decimais e diferença absoluta de até 0,02 L. Há 58 linhas com aferição positiva e nenhuma aferição nula.
- Apenas 26 dessas linhas coincidem com a hipótese de truncar separadamente os dois encerrantes antes da subtração, sob a mesma tolerância.
- As 180 linhas de tanques têm os dois campos de quantidade preenchidos; isso não comprova qual deles é usado como medição física no documento emitido.
- Duas consultas adicionais sobre todo o histórico não encontraram `LMC` sem produto correspondente nem `LMCBICOS` sem livro pai.

A tolerância de 0,02 L é exploratória, escolhida para comparação de dados armazenados; não é tolerância fiscal. Compatibilidade numérica indica um comportamento provável de armazenamento. Não demonstra o cálculo usado na impressão nem a validade dos dados de origem. A emissão da nova aplicação será validada contra as regras documentadas e casos de referência, sem exigir a execução do Xpert. Documentos históricos já disponíveis podem servir como evidência complementar.

As foreign keys inspecionadas estão habilitadas, mas marcadas como não confiáveis pelo SQL Server (`is_not_trusted=1`). Portanto, a existência do vínculo no esquema não substitui a conferência dos registros históricos. Nenhuma constraint foi alterada ou revalidada.

A comparação adicional das fontes de encerrantes confirmou 538 fechamentos coincidentes com `ENCERRANTESTURNOS` e apenas 6 com `ENCERRANTES`, em 540 linhas do mesmo recorte. Aberturas e aferições também foram comparadas. As diferenças, os campos de situação ainda não homologados e a proposta de captura estão no [relatório de encerrantes](encerrantes-fechamento.md).

A indicação posterior de `CONSOLEARQUIVO` pelo usuário foi testada: os últimos registros por bico e dia de `DATA` coincidem com os 540 fechamentos do LMC. Agrupar por `DATABAIXA` produz 537 coincidências. As duas divergências por turno correspondem a valores encontrados em abastecimentos do dia seguinte. O arquivo é agora a principal fonte candidata para o corte, preservando a conferência com os registros por turno. Empates, estados, abertura e cobertura continuam sujeitos às verificações detalhadas no relatório.

O usuário confirmou posteriormente que a automação atualiza os encerrantes ao registrar as vendas. O projeto usará essa alimentação existente e concentrará o trabalho na consulta, seleção do período e conferência para o LMC. O mecanismo exato de consolidação por turno ainda deve ser validado; não está previsto criar outro coletor de encerrantes nesta etapa.

## Origem do fechamento físico

O usuário informou que a abertura vem do fechamento do dia anterior e que o fechamento é lançado manualmente ou obtido por medidor eletrônico TLS (Gilbarco Veeder-Root na maioria dos clientes). A investigação ampliada confirmou seis leituras persistidas em `LEITURASTANQUES`, de 29/09/2026, além de 1.144 registros em `MOVTANQUES` e 915 em `ESTOQUETANQUELMC`. `AUTOLEITURATANQUES = 1` e existe configuração de medidor com `MODELO_MEDIDOR = 11`; a correspondência desse código com o TLS ainda não foi identificada.

Os seis volumes de 29/09 aparecem em `ESTOQUETANQUELMC` com data de 28/09. As comparações do histórico mostram que esse deslocamento não é uma regra universal. Os campos do LMC também divergem dos volumes dessa tabela no período comparado. Portanto, a persistência de leituras está confirmada, mas sua origem exata, a data operacional correspondente e o uso no fechamento ainda exigem validação.

O [relatório de leituras dos tanques](medicoes-tanques.md) registra evidências, exemplos e limitações, reproduzíveis pelo [script 04](../sql/04-origem-medicoes-tanques.sql). Foram feitas apenas consultas; nenhum equipamento foi acessado. A leitura do banco pode sustentar a nova interface, mas é necessário identificar qual processo continuará fornecendo novas leituras sem o desktop Xpert.

## Notas de entrada e turnos

A inspeção confirmou foreign keys de `COMPROVANTES` e `MOVPRODUTOS` para `TURNOS` usando `ID_TURNOS` + `ID_FILIAL`. A relação de `MOVPRODUTOS` com `COMPROVANTES` usa `ID_COMPROVANTE` + `ID_FILIAL` + `ID_DB`. A tabela de itens se chama `ITENSCOMPROVANTE` (singular); seu vínculo com o documento usa a mesma chave tripla, e o vínculo com `PRODUTOS` inclui filial.

`LMCENTRADATANQUES` tem referência ao documento e `DTACONTA`, mas não tem coluna de turno. Uma mudança operacional precisa considerar os campos relacionados, sem assumir que alterar o cabeçalho atualizará os demais dados.

Também foram encontradas triggers habilitadas e criptografadas em `COMPROVANTES`, `MOVPRODUTOS` e `LMCENTRADATANQUES`. A troca de turno não usará tabelas adicionais. Será necessário mapear a atualização conjunta das notas e movimentos existentes e o histórico disponível. A funcionalidade de alteração de registros está incluída no escopo; até este levantamento, foram realizadas somente consultas.

O diagnóstico de datas encontrou 1.000 documentos vinculados a 1.038 parcelas de entrada e 1.000 movimentos de produto. Cinco apresentam intervalo de pelo menos cinco dias entre `DTAEMISSAO` e `DTACONTA`. Todos apontam para o turno de ID 1, cadastrado em 25/03/2024, com número 0, inclusive os movimentos associados. O usuário confirmou que esse é o turno geral; sua data cadastral não é o dia de cada lançamento. Os exemplos e limites estão na [conferência de notas](notas-entrada-turnos.md#diagnóstico-de-datas-em-06102026).

`COMPENTRADAS` contém dados complementares e chave de acesso da NF-e, com vínculo ao documento pela chave tripla. `XMLENTRADAS` tem os mesmos identificadores e campo `DOC_XML`, mas não foi localizado XML vinculado a esses 1.000 documentos. O usuário esclareceu posteriormente que `COMPROVANTES.DATA` representa emissão e `DTAEMISSAO` representa entrada, enquanto o LMC segue a data operacional do lançamento. Essa interpretação decorre da confirmação do usuário, não do nome das colunas. O instante de digitação continua sem mapeamento.
