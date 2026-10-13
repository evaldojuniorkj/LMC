-- Somente leitura. Candidatos a revisao, nao erros comprovados ou propostas de escrita.
-- Preserva a compatibilidade 100 do ATXDADOS; nao requer alteracao de estrutura.
-- DTAEMISSAO, DATA e DTACONTA mantem seus nomes de origem: semantica a homologar.
SET NOCOUNT ON;
SET LOCK_TIMEOUT 5000;

DECLARE @Filial smallint = 17581;
DECLARE @DiasAtencao int = 5; -- Filtro exploratorio, nao prazo obrigatorio de transporte.
DECLARE @DataReferencia date = NULL; -- Informar explicitamente para conferir datas futuras.

SELECT DB_NAME() AS Banco, @Filial AS Filial, @DiasAtencao AS DiasAtencao,
       @DataReferencia AS DataReferenciaParaDatasFuturas;

-- Agrupar as parcelas primeiro para contar cada documento uma unica vez.
WITH entradas AS (
    SELECT ID_FILIAL, ID_DB, ID_COMPROVANTE, COUNT_BIG(*) AS Parcelas,
           MIN(DTACONTA) AS DataTanqueMin, MAX(DTACONTA) AS DataTanqueMax
    FROM dbo.LMCENTRADATANQUES
    WHERE ID_FILIAL = @Filial AND ID_COMPROVANTE IS NOT NULL
    GROUP BY ID_FILIAL, ID_DB, ID_COMPROVANTE
)
SELECT COUNT_BIG(*) AS Documentos, SUM(e.Parcelas) AS Parcelas,
       SUM(CASE WHEN c.ID_COMPROVANTE IS NULL THEN 1 ELSE 0 END) AS SemCabecalho,
       SUM(CASE WHEN c.DTAEMISSAO IS NULL THEN 1 ELSE 0 END) AS CampoDTAEMISSAOAusente,
       SUM(CASE WHEN c.DTACONTA IS NULL THEN 1 ELSE 0 END) AS CampoDTACONTAAusente,
       SUM(CASE WHEN DATEDIFF(day, c.DTAEMISSAO, c.DTACONTA) < 0 THEN 1 ELSE 0 END) AS DTACONTAAnteriorDTAEMISSAO,
       SUM(CASE WHEN DATEDIFF(day, c.DTAEMISSAO, c.DTACONTA) >= @DiasAtencao THEN 1 ELSE 0 END) AS IntervaloPeloMenosDiasAtencao,
       SUM(CASE WHEN CONVERT(date, e.DataTanqueMin) <> CONVERT(date, c.DTACONTA)
                     OR CONVERT(date, e.DataTanqueMax) <> CONVERT(date, c.DTACONTA) THEN 1 ELSE 0 END) AS DataTanqueDivergente,
       SUM(CASE WHEN c.ID_TURNOS IS NULL THEN 1 ELSE 0 END) AS TurnoNaoInformado,
       SUM(CASE WHEN c.ID_TURNOS IS NOT NULL AND u.ID_TURNOS IS NULL THEN 1 ELSE 0 END) AS TurnoNaoLocalizado,
       SUM(CASE WHEN CONVERT(date, u.DATATURNO) <> CONVERT(date, c.DTACONTA) THEN 1 ELSE 0 END) AS DataTurnoDivergente
FROM entradas e
LEFT JOIN dbo.COMPROVANTES c ON c.ID_FILIAL = e.ID_FILIAL AND c.ID_DB = e.ID_DB AND c.ID_COMPROVANTE = e.ID_COMPROVANTE
LEFT JOIN dbo.TURNOS u ON u.ID_FILIAL = c.ID_FILIAL AND u.ID_TURNOS = c.ID_TURNOS;

-- Os movimentos sao agregados por chave completa; nunca somar volumes apos join com parcelas.
WITH entradas AS (
    SELECT ID_FILIAL, ID_DB, ID_COMPROVANTE FROM dbo.LMCENTRADATANQUES
    WHERE ID_FILIAL = @Filial AND ID_COMPROVANTE IS NOT NULL
    GROUP BY ID_FILIAL, ID_DB, ID_COMPROVANTE
), movimentos AS (
    SELECT e.ID_FILIAL, e.ID_DB, e.ID_COMPROVANTE, COUNT_BIG(m.ID_COMPROVANTE) AS N,
           MIN(m.ID_TURNOS) AS TurnoMin, MAX(m.ID_TURNOS) AS TurnoMax,
           MIN(m.DTACONTA) AS DataMin, MAX(m.DTACONTA) AS DataMax,
           SUM(CASE WHEN m.ID_COMPROVANTE IS NOT NULL AND m.ID_TURNOS IS NULL THEN 1 ELSE 0 END) AS TurnosNulos,
           SUM(CASE WHEN m.ID_COMPROVANTE IS NOT NULL AND m.DTACONTA IS NULL THEN 1 ELSE 0 END) AS DatasNulas
    FROM entradas e LEFT JOIN dbo.MOVPRODUTOS m
      ON m.ID_FILIAL = e.ID_FILIAL AND m.ID_DB = e.ID_DB AND m.ID_COMPROVANTE = e.ID_COMPROVANTE
    GROUP BY e.ID_FILIAL, e.ID_DB, e.ID_COMPROVANTE
)
SELECT COUNT_BIG(*) AS Documentos, SUM(m.N) AS Movimentos,
       SUM(CASE WHEN m.N = 0 THEN 1 ELSE 0 END) AS SemMovimentos,
       SUM(CASE WHEN m.TurnoMin <> c.ID_TURNOS OR m.TurnoMax <> c.ID_TURNOS THEN 1 ELSE 0 END) AS TurnosMovimentoDiferentes,
       SUM(CASE WHEN CONVERT(date, m.DataMin) <> CONVERT(date, c.DTACONTA)
                     OR CONVERT(date, m.DataMax) <> CONVERT(date, c.DTACONTA) THEN 1 ELSE 0 END) AS DatasMovimentoDiferentes,
       SUM(m.TurnosNulos) AS MovimentosSemTurno, SUM(m.DatasNulas) AS MovimentosSemData
FROM movimentos m JOIN dbo.COMPROVANTES c
  ON c.ID_FILIAL = m.ID_FILIAL AND c.ID_DB = m.ID_DB AND c.ID_COMPROVANTE = m.ID_COMPROVANTE;

-- Lista priorizada por distancia entre os campos. Nao presumir a data correta de destino.
WITH entradas AS (
    SELECT ID_FILIAL, ID_DB, ID_COMPROVANTE, MIN(DTACONTA) AS DataTanqueMin,
           MAX(DTACONTA) AS DataTanqueMax, COUNT_BIG(*) AS Parcelas
    FROM dbo.LMCENTRADATANQUES WHERE ID_FILIAL = @Filial AND ID_COMPROVANTE IS NOT NULL
    GROUP BY ID_FILIAL, ID_DB, ID_COMPROVANTE
)
SELECT TOP (50) c.ID_COMPROVANTE, c.ID_FILIAL, c.ID_DB, c.NROCOMPROVANTE,
       CONVERT(date, c.DATA) AS CampoDATA, CONVERT(date, c.DTAEMISSAO) AS CampoDTAEMISSAO,
       CONVERT(date, c.DTACONTA) AS CampoDTACONTA,
       DATEDIFF(day, c.DTAEMISSAO, c.DTACONTA) AS DiasEntreCampos,
       c.ID_TURNOS, CONVERT(date, u.DATATURNO) AS DataCadastroTurno, u.TURNO,
       CONVERT(date, e.DataTanqueMin) AS DataTanqueMin, CONVERT(date, e.DataTanqueMax) AS DataTanqueMax,
       e.Parcelas,
       CASE WHEN @DataReferencia IS NULL THEN NULL
            WHEN CONVERT(date, c.DTACONTA) > @DataReferencia THEN 1 ELSE 0 END AS DataFuturaEmRelacaoReferencia
FROM entradas e JOIN dbo.COMPROVANTES c
  ON c.ID_FILIAL = e.ID_FILIAL AND c.ID_DB = e.ID_DB AND c.ID_COMPROVANTE = e.ID_COMPROVANTE
LEFT JOIN dbo.TURNOS u ON u.ID_FILIAL = c.ID_FILIAL AND u.ID_TURNOS = c.ID_TURNOS
WHERE DATEDIFF(day, c.DTAEMISSAO, c.DTACONTA) >= @DiasAtencao
   OR DATEDIFF(day, c.DTAEMISSAO, c.DTACONTA) < 0
   OR CONVERT(date, e.DataTanqueMin) <> CONVERT(date, c.DTACONTA)
   OR CONVERT(date, e.DataTanqueMax) <> CONVERT(date, c.DTACONTA)
   OR (@DataReferencia IS NOT NULL AND CONVERT(date, c.DTACONTA) > @DataReferencia)
ORDER BY ABS(DATEDIFF(day, c.DTAEMISSAO, c.DTACONTA)) DESC, c.DTACONTA DESC, c.ID_COMPROVANTE;

-- Verificar cobertura de XML por documento, sem ler ou imprimir o conteudo fiscal completo.
WITH entradas AS (
    SELECT ID_FILIAL, ID_DB, ID_COMPROVANTE FROM dbo.LMCENTRADATANQUES
    WHERE ID_FILIAL = @Filial AND ID_COMPROVANTE IS NOT NULL
    GROUP BY ID_FILIAL, ID_DB, ID_COMPROVANTE
)
SELECT COUNT_BIG(*) AS Documentos,
       SUM(CASE WHEN x.N > 0 THEN 1 ELSE 0 END) AS ComXMLVinculado,
       SUM(CASE WHEN x.N > 1 THEN 1 ELSE 0 END) AS ComMaisDeUmXML
FROM entradas e OUTER APPLY (
    SELECT COUNT_BIG(*) AS N FROM dbo.XMLENTRADAS x
    WHERE x.ID_FILIAL = e.ID_FILIAL AND x.ID_DB = e.ID_DB AND x.ID_COMPROVANTE = e.ID_COMPROVANTE
) x;

WITH entradas AS (
    SELECT ID_FILIAL, ID_DB, ID_COMPROVANTE FROM dbo.LMCENTRADATANQUES
    WHERE ID_FILIAL = @Filial AND ID_COMPROVANTE IS NOT NULL
    GROUP BY ID_FILIAL, ID_DB, ID_COMPROVANTE
)
SELECT c.ID_TURNOS, u.DATATURNO, u.TURNO, COUNT_BIG(*) AS Documentos
FROM entradas e JOIN dbo.COMPROVANTES c
  ON c.ID_FILIAL = e.ID_FILIAL AND c.ID_DB = e.ID_DB AND c.ID_COMPROVANTE = e.ID_COMPROVANTE
LEFT JOIN dbo.TURNOS u ON u.ID_FILIAL = c.ID_FILIAL AND u.ID_TURNOS = c.ID_TURNOS
GROUP BY c.ID_TURNOS, u.DATATURNO, u.TURNO;

SELECT COUNT_BIG(*) AS ParcelasSemDocumento
FROM dbo.LMCENTRADATANQUES WHERE ID_FILIAL = @Filial AND ID_COMPROVANTE IS NULL;
