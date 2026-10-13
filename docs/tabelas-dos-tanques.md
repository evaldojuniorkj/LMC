# Tabelas dos tanques

No menu **Tabelas dos tanques**, escolha um CSV, dê um nome e clique em **Conferir arquivo**. A prévia informa quantidade de medidas, intervalo em centímetros e litros, além das primeiras e últimas linhas. **Importar tabela** adiciona a tabela à biblioteca. Em cada tanque, selecione a tabela correspondente e clique em **Salvar escolha**. A opção **Sem tabela vinculada** remove a associação, mantendo o arquivo disponível na biblioteca.

O primeiro campo representa altura em **centímetros**, o segundo representa volume em **litros**. São aceitos ponto e vírgula, tabulação ou vírgula como delimitador; cabeçalho é opcional (`Altura (cm);Volume (L)`). Com vírgula delimitadora, use ponto decimal. Com os outros delimitadores, aceita ponto ou vírgula decimal. Não use separador de milhares. Uma coluna vazia final é aceita. Alturas e volumes devem crescer, sem repetições, com até quatro casas decimais, entre 2 e 10.000 medidas e no máximo 512 KB por arquivo. A interface lê UTF-8 ou Windows-1252. Os pares são preservados sem interpolação nem ajuste de volume.

Exemplo de formato (ilustrativo, não uma tabela de calibração):

```csv
Altura (cm);Volume (L)
0;0
1,5;12,3456
```

Uma tabela pode atender vários tanques, mas cada associação é explícita. Capacidade igual não garante o mesmo modelo. Os vínculos são separados por servidor/banco, filial, tanque e produto. Se o produto cadastrado mudar, o vínculo antigo não é usado. No LMC, **Tabela próxima** informa a referência do tanque e o nome da tabela. Um tanque sem vínculo mostra **Sem tabela**. Leituras, abertura, fechamento e LMCs gravados permanecem independentes das tabelas.

## Armazenamento e backup

Não há tabelas, campos, registros de configuração ou arquivos CSV no SQL Server. A única consulta nova ao banco lê o cadastro de tanques e produtos para montar a seleção. A biblioteca (nome, arquivo de origem, medidas, data de importação e hash do conteúdo) e os vínculos são gravados juntos em `catalog.json`.

- Desenvolvimento: `src/Lmc.Web/App_Data/tank-tables/catalog.json`.
- Instalação com `ProtectedConfig`: subpasta `tank-tables` ao lado de `configuration.bin`, normalmente `%ProgramData%\LMC\tank-tables\catalog.json`. Independe da pasta da versão do aplicativo.
- Opcionalmente, `TankTables:Directory` configura um caminho absoluto alternativo.

Inclua essa pasta no backup e restaure-a no servidor da aplicação. Todos os navegadores conectados ao mesmo servidor compartilham a biblioteca. Não basta fazer backup do ATXDADOS para recuperar essas configurações. A pasta de desenvolvimento é excluída do Git e dos pacotes de publicação.

Os scripts existentes de instalação e atualização chamam `--PrepareOperatorAccess true`; essa preparação também cria a pasta das tabelas e dá permissão de alteração ao `LocalService`, preservando os arquivos existentes. Em uma atualização manual dos DLLs, execute essa preparação como administrador com o mesmo `--ProtectedConfig` antes de iniciar o serviço. A preparação não altera SQL Server.

Gravações usam arquivo temporário e substituição atômica, trava de arquivo entre processos e revisão para detectar outra sessão. Reimportar as mesmas medidas reutiliza a tabela existente. Falhas de leitura ou arquivos inválidos não reinicializam o catálogo. Alterações de vínculos não modificam a tabela original; para uma tabela revisada, importe o novo CSV e escolha-o nos tanques correspondentes.

## Verificação

`dotnet run --project tests/Lmc.Checks -p:UseAppHost=false` cobre formatos, limites, precisão, persistência, isolamento dos vínculos, conflito entre sessões e arquivo danificado, sem acesso ao SQL Server. `node tests/verificar-tabela-tanque.mjs` cobre proximidade, empates, ausência de vínculo, tanques do mesmo combustível usando tabelas distintas e preservação das medições.
