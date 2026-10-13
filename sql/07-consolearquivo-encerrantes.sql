-- Somente leitura: testa a origem dos encerrantes no historico de abastecimentos.
-- O recorte por DATA usa [00:00 do dia, 00:00 do dia seguinte).
-- E uma hipotese de corte validada nesta amostra, nao uma regra para todos os postos.
-- Nao filtra codigos STATUS/ABASTECIMENTO_GERADO ainda sem semantica homologada.
SET NOCOUNT ON;
SET LOCK_TIMEOUT 5000;
DECLARE @Filial smallint = 17581;
DECLARE @Fim date = (SELECT CONVERT(date, MAX(DTACONTA)) FROM dbo.LMC WHERE ID_FILIAL = @Filial);
DECLARE @Inicio date = DATEADD(day, -29, @Fim);
DECLARE @Tolerancia decimal(19,4) = 0.02; -- Comparacao numerica exploratoria.

SELECT @Filial AS Filial, @Inicio AS Inicio, @Fim AS Fim, @Tolerancia AS ToleranciaExploratoria;

-- O ID desempata a listagem. A consulta de empates abaixo verifica se o valor e ambiguo.
WITH abastecimentos AS (
    SELECT ID_FILIAL, ID_BICOS, CONVERT(date, DATA) AS Dia, ENCERRANTE, ENCERRANTEANTERIOR, QTDE,
           ROW_NUMBER() OVER (PARTITION BY ID_FILIAL, ID_BICOS, CONVERT(date, DATA) ORDER BY DATA, ID_CONSOLEARQUIVO) AS Primeiro,
           ROW_NUMBER() OVER (PARTITION BY ID_FILIAL, ID_BICOS, CONVERT(date, DATA) ORDER BY DATA DESC, ID_CONSOLEARQUIVO DESC) AS Ultimo
    FROM dbo.CONSOLEARQUIVO WHERE ID_FILIAL = @Filial AND DATA >= @Inicio AND DATA < DATEADD(day, 1, @Fim)
), arquivo AS (
    SELECT ID_BICOS, Dia, COUNT_BIG(*) AS N,
           MAX(CASE WHEN Primeiro = 1 THEN ENCERRANTEANTERIOR END) AS Abertura,
           MAX(CASE WHEN Ultimo = 1 THEN ENCERRANTE END) AS Fechamento,
           SUM(CONVERT(decimal(19,4), QTDE)) AS Volume
    FROM abastecimentos GROUP BY ID_BICOS, Dia
), turnos AS (
    SELECT e.ID_BICOS, CONVERT(date, t.DATATURNO) AS Dia, COUNT_BIG(*) AS N,
           MAX(e.ENCERRANTEFECHAMENTO) AS Fechamento
    FROM dbo.ENCERRANTESTURNOS e JOIN dbo.TURNOS t ON t.ID_FILIAL = e.ID_FILIAL AND t.ID_TURNOS = e.ID_TURNOS
    WHERE e.ID_FILIAL = @Filial GROUP BY e.ID_BICOS, CONVERT(date, t.DATATURNO)
)
SELECT COUNT_BIG(*) AS LinhasLMC,
       SUM(CASE WHEN a.N > 0 THEN 1 ELSE 0 END) AS ComAbastecimentos,
       SUM(CASE WHEN ABS(a.Abertura - CONVERT(decimal(19,4), b.ENCERRANTEABERTURA)) <= @Tolerancia THEN 1 ELSE 0 END) AS AberturasIguaisArquivo,
       SUM(CASE WHEN ABS(a.Fechamento - CONVERT(decimal(19,4), b.ENCERRANTEFECHAMENTO)) <= @Tolerancia THEN 1 ELSE 0 END) AS FechamentosIguaisArquivo,
       SUM(CASE WHEN t.N = 1 AND ABS(a.Fechamento - t.Fechamento) <= @Tolerancia THEN 1 ELSE 0 END) AS FechamentosArquivoIguaisTurno,
       SUM(CASE WHEN ABS(a.Volume - CONVERT(decimal(19,4), b.VENDAS)) <= @Tolerancia THEN 1 ELSE 0 END) AS SomaQtdeIgualVendasLMC
FROM dbo.LMC l JOIN dbo.LMCBICOS b ON b.ID_FILIAL = l.ID_FILIAL AND b.ID_LMC = l.ID_LMC
LEFT JOIN arquivo a ON a.ID_BICOS = b.ID_BICOS AND a.Dia = CONVERT(date, l.DTACONTA)
LEFT JOIN turnos t ON t.ID_BICOS = b.ID_BICOS AND t.Dia = CONVERT(date, l.DTACONTA)
WHERE l.ID_FILIAL = @Filial AND l.DTACONTA >= @Inicio AND l.DTACONTA < DATEADD(day, 1, @Fim);

-- Testar DATA e DATABAIXA sem pressupor que representem o mesmo evento.
WITH candidatos AS (
    SELECT c.ID_BICOS, d.BaseData, d.Dia, c.ENCERRANTE,
           ROW_NUMBER() OVER (PARTITION BY c.ID_BICOS, d.BaseData, d.Dia ORDER BY c.DATA DESC, c.ID_CONSOLEARQUIVO DESC) AS Ultimo
    FROM dbo.CONSOLEARQUIVO c
    CROSS APPLY (VALUES ('DATA', CONVERT(date, c.DATA)), ('DATABAIXA', CONVERT(date, c.DATABAIXA))) d(BaseData, Dia)
    WHERE c.ID_FILIAL = @Filial AND d.Dia BETWEEN @Inicio AND @Fim
)
SELECT c.BaseData, COUNT_BIG(*) AS LinhasLMC,
       SUM(CASE WHEN ABS(c.ENCERRANTE - CONVERT(decimal(19,4), b.ENCERRANTEFECHAMENTO)) <= @Tolerancia THEN 1 ELSE 0 END) AS FechamentosCoincidentes
FROM dbo.LMC l JOIN dbo.LMCBICOS b ON b.ID_FILIAL = l.ID_FILIAL AND b.ID_LMC = l.ID_LMC
JOIN candidatos c ON c.ID_BICOS = b.ID_BICOS AND c.Dia = CONVERT(date, l.DTACONTA) AND c.Ultimo = 1
WHERE l.ID_FILIAL = @Filial AND l.DTACONTA >= @Inicio AND l.DTACONTA < DATEADD(day, 1, @Fim)
GROUP BY c.BaseData;

SELECT STATUS, ABASTECIMENTO_GERADO, REGALT, COUNT_BIG(*) AS Registros,
       SUM(CASE WHEN CONVERT(date, DATA) <> CONVERT(date, DATABAIXA) THEN 1 ELSE 0 END) AS BaixaEmOutroDia,
       SUM(CASE WHEN ABS(CONVERT(decimal(19,4), ENCERRANTE - ENCERRANTEANTERIOR) - CONVERT(decimal(19,4), QTDE)) > @Tolerancia THEN 1 ELSE 0 END) AS DeltaDiferenteQtde
FROM dbo.CONSOLEARQUIVO WHERE ID_FILIAL = @Filial AND DATA >= @Inicio AND DATA < DATEADD(day, 1, @Fim)
GROUP BY STATUS, ABASTECIMENTO_GERADO, REGALT ORDER BY STATUS, ABASTECIMENTO_GERADO, REGALT;

WITH ultimos AS (
    SELECT ID_BICOS, CONVERT(date, DATA) AS Dia, DATA, ENCERRANTE, ENCERRANTEANTERIOR,
           DENSE_RANK() OVER (PARTITION BY ID_BICOS, CONVERT(date, DATA) ORDER BY DATA DESC) AS Ordem
    FROM dbo.CONSOLEARQUIVO WHERE ID_FILIAL = @Filial AND DATA >= @Inicio AND DATA < DATEADD(day, 1, @Fim)
), grupos AS (
    SELECT ID_BICOS, Dia, COUNT_BIG(*) AS N,
           COUNT(DISTINCT ENCERRANTE) AS ValoresFechamento, COUNT(DISTINCT ENCERRANTEANTERIOR) AS ValoresAnterior
    FROM ultimos WHERE Ordem = 1 GROUP BY ID_BICOS, Dia
)
SELECT COUNT_BIG(*) AS BicosDiasArquivo,
       SUM(CASE WHEN N > 1 THEN 1 ELSE 0 END) AS EmpatesNoUltimoHorario,
       SUM(CASE WHEN N > 1 AND (ValoresFechamento > 1 OR ValoresAnterior > 1) THEN 1 ELSE 0 END) AS EmpatesComValoresDistintos
FROM grupos;

-- Diferencas da abertura candidata (anterior do primeiro abastecimento).
WITH primeiros AS (
    SELECT ID_BICOS, DATA, ENCERRANTEANTERIOR,
           ROW_NUMBER() OVER (PARTITION BY ID_BICOS, CONVERT(date, DATA) ORDER BY DATA, ID_CONSOLEARQUIVO) AS Ordem
    FROM dbo.CONSOLEARQUIVO WHERE ID_FILIAL = @Filial AND DATA >= @Inicio AND DATA < DATEADD(day, 1, @Fim)
)
SELECT CONVERT(date, l.DTACONTA) AS Dia, b.ID_BICOS,
       CONVERT(decimal(19,4), b.ENCERRANTEABERTURA) AS AberturaLMC,
       c.DATA AS PrimeiroRegistro, c.ENCERRANTEANTERIOR AS AnteriorPrimeiroRegistro,
       CONVERT(decimal(19,4), b.ENCERRANTEABERTURA) - c.ENCERRANTEANTERIOR AS Diferenca
FROM dbo.LMC l JOIN dbo.LMCBICOS b ON b.ID_FILIAL = l.ID_FILIAL AND b.ID_LMC = l.ID_LMC
JOIN primeiros c ON c.ID_BICOS = b.ID_BICOS AND CONVERT(date, c.DATA) = CONVERT(date, l.DTACONTA) AND c.Ordem = 1
WHERE l.ID_FILIAL = @Filial AND ABS(CONVERT(decimal(19,4), b.ENCERRANTEABERTURA) - c.ENCERRANTEANTERIOR) > @Tolerancia
ORDER BY l.DTACONTA, b.ID_BICOS;

-- Procurar, no dia seguinte, valores que coincidam com as divergencias por turno.
-- A coincidencia demonstra associacao numerica, nao qual rotina copiou o valor.
WITH turnos AS (
    SELECT e.ID_BICOS, CONVERT(date, t.DATATURNO) AS Dia, COUNT_BIG(*) AS N,
           MAX(e.ID_TURNOS) AS ID_TURNOS, MAX(e.ENCERRANTEFECHAMENTO) AS Fechamento
    FROM dbo.ENCERRANTESTURNOS e JOIN dbo.TURNOS t ON t.ID_FILIAL = e.ID_FILIAL AND t.ID_TURNOS = e.ID_TURNOS
    WHERE e.ID_FILIAL = @Filial GROUP BY e.ID_BICOS, CONVERT(date, t.DATATURNO)
)
SELECT CONVERT(date, l.DTACONTA) AS DiaLMC, b.ID_BICOS, t.ID_TURNOS,
       CONVERT(decimal(19,4), b.ENCERRANTEFECHAMENTO) AS FechamentoLMC,
       t.Fechamento AS FechamentoTurno, c.ID_CONSOLEARQUIVO,
       c.DATA AS RegistroDiaSeguinte, c.ENCERRANTEANTERIOR, c.ENCERRANTE
FROM dbo.LMC l JOIN dbo.LMCBICOS b ON b.ID_FILIAL = l.ID_FILIAL AND b.ID_LMC = l.ID_LMC
JOIN turnos t ON t.ID_BICOS = b.ID_BICOS AND t.Dia = CONVERT(date, l.DTACONTA) AND t.N = 1
LEFT JOIN dbo.CONSOLEARQUIVO c ON c.ID_FILIAL = b.ID_FILIAL AND c.ID_BICOS = b.ID_BICOS
  AND c.DATA >= DATEADD(day, 1, CONVERT(date, l.DTACONTA)) AND c.DATA < DATEADD(day, 2, CONVERT(date, l.DTACONTA))
  AND ABS(c.ENCERRANTE - t.Fechamento) <= @Tolerancia
WHERE l.ID_FILIAL = @Filial AND l.DTACONTA >= @Inicio AND l.DTACONTA < DATEADD(day, 1, @Fim)
  AND ABS(CONVERT(decimal(19,4), b.ENCERRANTEFECHAMENTO) - t.Fechamento) > @Tolerancia
ORDER BY l.DTACONTA, b.ID_BICOS, c.DATA;
