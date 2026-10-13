-- Consulta somente leitura, compatível com o nível 100 do banco legado.
WITH entries AS (
    SELECT e.ID_FILIAL, e.ID_DB, e.ID_COMPROVANTE, COUNT(*) AS Parcels,
           SUM(CONVERT(decimal(19, 4), e.QTDE)) AS Volume,
           SUM(CASE WHEN e.QTDE IS NULL OR e.DTACONTA IS NULL THEN 1 ELSE 0 END) AS Missing,
           MIN(e.DTACONTA) AS DateMin, MAX(e.DTACONTA) AS DateMax
    FROM dbo.LMCENTRADATANQUES e
    WHERE e.ID_FILIAL = @branch AND e.ID_COMPROVANTE IS NOT NULL
      AND (@id IS NULL OR e.ID_COMPROVANTE = @id)
      AND (@db IS NULL OR e.ID_DB = @db)
    GROUP BY e.ID_FILIAL, e.ID_DB, e.ID_COMPROVANTE
)
SELECT c.ID_COMPROVANTE AS Id, c.ID_FILIAL AS BranchId, c.ID_DB AS DatabaseId,
       c.NROCOMPROVANTE AS Number,
       COALESCE(NULLIF(RTRIM(s.NOMEENTIDADE), ''), RTRIM(s.RAZAOSOCIALENTIDADE), 'Fornecedor não localizado') AS Supplier,
       COALESCE(STUFF((SELECT DISTINCT ', ' + COALESCE(RTRIM(p.NOMEPRODUTO), 'Produto sem cadastro')
          FROM dbo.LMCENTRADATANQUES ep
          LEFT JOIN dbo.PRODUTOS p ON p.ID_FILIAL=ep.ID_FILIAL AND p.ID_PRODUTOS=ep.ID_PRODUTOS
          WHERE ep.ID_FILIAL=e.ID_FILIAL AND ep.ID_DB=e.ID_DB AND ep.ID_COMPROVANTE=e.ID_COMPROVANTE
          FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, ''), '') AS Products,
       CONVERT(char(10), c.DATA, 23) AS DocumentDate,
       CONVERT(char(10), c.DTAEMISSAO, 23) AS IssueFieldDate,
       CONVERT(char(10), c.DTACONTA, 23) AS OperationalDate,
       c.ID_TURNOS AS ShiftId, CONVERT(int, t.TURNO) AS ShiftNumber,
       CONVERT(char(10), t.DATATURNO, 23) AS ShiftDate,
       CASE WHEN e.Missing > 0 THEN NULL ELSE e.Volume END AS Volume,
       e.Parcels, m.N AS Movements,
       CONVERT(bit, CASE WHEN e.Missing > 0 OR m.N = 0 OR m.Missing > 0
          OR m.ShiftMin <> c.ID_TURNOS OR m.ShiftMax <> c.ID_TURNOS
          OR CONVERT(date, m.DateMin) <> CONVERT(date, c.DTACONTA)
          OR CONVERT(date, m.DateMax) <> CONVERT(date, c.DTACONTA)
          OR CONVERT(date, e.DateMin) <> CONVERT(date, c.DTACONTA)
          OR CONVERT(date, e.DateMax) <> CONVERT(date, c.DTACONTA) THEN 1 ELSE 0 END) AS Inconsistent,
       CONVERT(varchar(30), c.DATAREPL, 126) AS ReplicationDate
FROM entries e
JOIN dbo.COMPROVANTES c ON c.ID_FILIAL=e.ID_FILIAL AND c.ID_DB=e.ID_DB AND c.ID_COMPROVANTE=e.ID_COMPROVANTE
LEFT JOIN dbo.ENTIDADES s ON s.ID_FILIAL=c.ID_FILIAL AND s.ID_ENTIDADE=c.ID_ENTIDADE
LEFT JOIN dbo.TURNOS t ON t.ID_FILIAL=c.ID_FILIAL AND t.ID_TURNOS=c.ID_TURNOS
OUTER APPLY (
    SELECT COUNT(*) AS N, SUM(CASE WHEN mp.DTACONTA IS NULL OR mp.ID_TURNOS IS NULL THEN 1 ELSE 0 END) AS Missing,
           MIN(mp.DTACONTA) AS DateMin, MAX(mp.DTACONTA) AS DateMax,
           MIN(mp.ID_TURNOS) AS ShiftMin, MAX(mp.ID_TURNOS) AS ShiftMax
    FROM dbo.MOVPRODUTOS mp
    WHERE mp.ID_FILIAL=c.ID_FILIAL AND mp.ID_DB=c.ID_DB AND mp.ID_COMPROVANTE=c.ID_COMPROVANTE
) m
ORDER BY c.ID_COMPROVANTE;
