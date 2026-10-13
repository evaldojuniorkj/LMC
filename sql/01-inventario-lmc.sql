-- Executar no banco selecionado explicitamente pelo cliente.
-- Somente SELECT e configuracoes da sessao; nao executa rotinas do Xpert.
SET NOCOUNT ON;
SET LOCK_TIMEOUT 5000;

SELECT DB_NAME() AS Banco,
       CAST(SERVERPROPERTY('ServerName') AS nvarchar(128)) AS Servidor,
       CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)) AS Versao;

SELECT ID_FILIAL, COUNT_BIG(*) AS Livros,
       COUNT(DISTINCT ID_PRODUTOS) AS Produtos,
       MIN(DTACONTA) AS Inicio, MAX(DTACONTA) AS Fim
FROM dbo.LMC
GROUP BY ID_FILIAL
ORDER BY ID_FILIAL;

SELECT t.name AS Tabela, c.column_id AS Ordem, c.name AS Coluna,
       ty.name AS Tipo, c.max_length AS Tamanho,
       c.precision AS Precisao, c.scale AS Escala, c.is_nullable AS AceitaNulo
FROM sys.tables AS t
JOIN sys.schemas AS s ON s.schema_id = t.schema_id
JOIN sys.columns AS c ON c.object_id = t.object_id
JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
WHERE s.name = 'dbo'
  AND t.name IN ('LMC', 'LMCBICOS', 'LMCTANQUES', 'LMCCOMPROVANTES',
                 'LMCENTRADATANQUES', 'ENCERRANTES', 'ESTOQUETANQUELMC',
                 'CFGLMC', 'BICOS', 'TANQUES')
ORDER BY t.name, c.column_id;

SELECT fk.name AS Vinculo,
       OBJECT_NAME(fk.parent_object_id) AS TabelaOrigem,
       pc.name AS ColunaOrigem,
       OBJECT_NAME(fk.referenced_object_id) AS TabelaDestino,
       rc.name AS ColunaDestino,
       fk.is_disabled AS Desabilitada,
       fk.is_not_trusted AS NaoConfiavel
FROM sys.foreign_keys AS fk
JOIN sys.foreign_key_columns AS fc ON fc.constraint_object_id = fk.object_id
JOIN sys.columns AS pc ON pc.object_id = fc.parent_object_id AND pc.column_id = fc.parent_column_id
JOIN sys.columns AS rc ON rc.object_id = fc.referenced_object_id AND rc.column_id = fc.referenced_column_id
WHERE OBJECT_NAME(fk.parent_object_id) IN ('LMC', 'LMCBICOS', 'LMCTANQUES', 'LMCCOMPROVANTES', 'LMCENTRADATANQUES')
ORDER BY fk.name, fc.constraint_column_id;

SELECT SCHEMA_NAME(o.schema_id) AS Esquema, o.name AS Rotina, o.type_desc AS Tipo,
       OBJECTPROPERTY(o.object_id, 'IsEncrypted') AS Criptografada
FROM sys.objects AS o
WHERE o.name LIKE '%LMC%' AND o.type IN ('P', 'TR', 'V')
ORDER BY o.type_desc, o.name;
