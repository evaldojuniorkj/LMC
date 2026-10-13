-- Somente leitura: compara fontes existentes, sem gerar ou corrigir LMC.
-- 0.02 L e tolerancia exploratoria entre tipos numericos, nao normativa.
SET NOCOUNT ON;
SET LOCK_TIMEOUT 5000;
DECLARE @Filial smallint = 17581;
DECLARE @Fim date = (SELECT CONVERT(date, MAX(DTACONTA)) FROM dbo.LMC WHERE ID_FILIAL = @Filial);
DECLARE @Inicio date = DATEADD(day, -29, @Fim);
DECLARE @Tolerancia decimal(19,4) = 0.02;

SELECT @Filial AS Filial, @Inicio AS Inicio, @Fim AS Fim;
SELECT ID_CFGLMC, CONVERT(varchar(8), HORACAPTURA, 108) AS HorarioCaptura,
       CAPTURATURNO, CAPTURAENCERRANTES, CALCENCBOMBA, USARULTIMOLMC
FROM dbo.CFGLMC;
SELECT COUNT_BIG(*) AS EncerrantesDiarios, MIN(DTACONTA) AS Inicio, MAX(DTACONTA) AS Fim,
       SUM(CASE WHEN ENCERRANTEFECHAMENTO IS NULL THEN 1 ELSE 0 END) AS FechamentosNulos,
       SUM(CASE WHEN ENCERRANTEFECHAMENTO = 0 THEN 1 ELSE 0 END) AS FechamentosZero
FROM dbo.ENCERRANTES WHERE ID_FILIAL = @Filial;
SELECT COUNT_BIG(*) AS EncerrantesTurnos, MIN(t.DATATURNO) AS Inicio, MAX(t.DATATURNO) AS Fim,
       SUM(CASE WHEN e.ENCERRANTEFECHAMENTO IS NULL THEN 1 ELSE 0 END) AS FechamentosNulos,
       SUM(CASE WHEN e.ENCERRANTEFECHAMENTO = 0 THEN 1 ELSE 0 END) AS FechamentosZero
FROM dbo.ENCERRANTESTURNOS e LEFT JOIN dbo.TURNOS t ON t.ID_FILIAL = e.ID_FILIAL AND t.ID_TURNOS = e.ID_TURNOS
WHERE e.ID_FILIAL = @Filial;

-- Comparar apenas grupos com uma linha; MAX nao escolhe o encerrante do dia.
WITH diarios AS (
    SELECT ID_BICOS, CONVERT(date, DTACONTA) AS Dia, COUNT_BIG(*) AS N,
           MAX(CONVERT(decimal(19,4), ENCERRANTEABERTURA)) AS Abertura,
           MAX(CONVERT(decimal(19,4), ENCERRANTEFECHAMENTO)) AS Fechamento
    FROM dbo.ENCERRANTES WHERE ID_FILIAL = @Filial GROUP BY ID_BICOS, CONVERT(date, DTACONTA)
), porTurno AS (
    SELECT e.ID_BICOS, CONVERT(date, t.DATATURNO) AS Dia, COUNT_BIG(*) AS N,
           MAX(CONVERT(decimal(19,4), e.ENCERRANTEABERTURA)) AS Abertura,
           MAX(CONVERT(decimal(19,4), e.ENCERRANTEFECHAMENTO)) AS Fechamento,
           MAX(CONVERT(decimal(19,4), e.AFERICAO)) AS Afericao
    FROM dbo.ENCERRANTESTURNOS e JOIN dbo.TURNOS t ON t.ID_FILIAL = e.ID_FILIAL AND t.ID_TURNOS = e.ID_TURNOS
    WHERE e.ID_FILIAL = @Filial GROUP BY e.ID_BICOS, CONVERT(date, t.DATATURNO)
)
SELECT COUNT_BIG(*) AS LinhasLMC,
       SUM(CASE WHEN d.N = 1 THEN 1 ELSE 0 END) AS ComDiarioUnico,
       SUM(CASE WHEN d.N = 1 AND ABS(d.Abertura - CONVERT(decimal(19,4), b.ENCERRANTEABERTURA)) <= @Tolerancia THEN 1 ELSE 0 END) AS AberturasIguaisDiario,
       SUM(CASE WHEN d.N = 1 AND ABS(d.Fechamento - CONVERT(decimal(19,4), b.ENCERRANTEFECHAMENTO)) <= @Tolerancia THEN 1 ELSE 0 END) AS FechamentosIguaisDiario,
       SUM(CASE WHEN p.N = 1 THEN 1 ELSE 0 END) AS ComTurnoUnico,
       SUM(CASE WHEN p.N > 1 THEN 1 ELSE 0 END) AS MaisDeUmRegistroTurno,
       SUM(CASE WHEN p.N = 1 AND ABS(p.Abertura - CONVERT(decimal(19,4), b.ENCERRANTEABERTURA)) <= @Tolerancia THEN 1 ELSE 0 END) AS AberturasIguaisTurno,
       SUM(CASE WHEN p.N = 1 AND ABS(p.Fechamento - CONVERT(decimal(19,4), b.ENCERRANTEFECHAMENTO)) <= @Tolerancia THEN 1 ELSE 0 END) AS FechamentosIguaisTurno,
       SUM(CASE WHEN p.N = 1 AND ABS(p.Afericao - CONVERT(decimal(19,4), b.AFERICAO)) <= @Tolerancia THEN 1 ELSE 0 END) AS AfericoesIguaisTurno
FROM dbo.LMC l JOIN dbo.LMCBICOS b ON b.ID_FILIAL = l.ID_FILIAL AND b.ID_LMC = l.ID_LMC
LEFT JOIN diarios d ON d.ID_BICOS = b.ID_BICOS AND d.Dia = CONVERT(date, l.DTACONTA)
LEFT JOIN porTurno p ON p.ID_BICOS = b.ID_BICOS AND p.Dia = CONVERT(date, l.DTACONTA)
WHERE l.ID_FILIAL = @Filial AND l.DTACONTA >= @Inicio AND l.DTACONTA < DATEADD(day, 1, @Fim);

WITH porTurno AS (
    SELECT e.ID_BICOS, CONVERT(date, t.DATATURNO) AS Dia, COUNT_BIG(*) AS N,
           MAX(e.ID_TURNOS) AS ID_TURNOS, MAX(e.ENCERRANTEFECHAMENTO) AS Fechamento
    FROM dbo.ENCERRANTESTURNOS e JOIN dbo.TURNOS t ON t.ID_FILIAL = e.ID_FILIAL AND t.ID_TURNOS = e.ID_TURNOS
    WHERE e.ID_FILIAL = @Filial GROUP BY e.ID_BICOS, CONVERT(date, t.DATATURNO)
)
SELECT CONVERT(date, l.DTACONTA) AS Dia, b.ID_BICOS, b.ID_LMC, p.ID_TURNOS,
       CONVERT(decimal(19,4), b.ENCERRANTEFECHAMENTO) AS FechamentoLMC, p.Fechamento AS FechamentoTurno,
       CONVERT(decimal(19,4), b.ENCERRANTEFECHAMENTO) - p.Fechamento AS Diferenca
FROM dbo.LMC l JOIN dbo.LMCBICOS b ON b.ID_FILIAL = l.ID_FILIAL AND b.ID_LMC = l.ID_LMC
JOIN porTurno p ON p.ID_BICOS = b.ID_BICOS AND p.Dia = CONVERT(date, l.DTACONTA) AND p.N = 1
WHERE l.ID_FILIAL = @Filial AND l.DTACONTA >= @Inicio AND l.DTACONTA < DATEADD(day, 1, @Fim)
  AND ABS(CONVERT(decimal(19,4), b.ENCERRANTEFECHAMENTO) - p.Fechamento) > @Tolerancia
ORDER BY l.DTACONTA, b.ID_BICOS;

SELECT COUNT_BIG(*) AS TurnosNoPeriodo,
       SUM(CASE WHEN DATAFECHAMENTO IS NULL THEN 1 ELSE 0 END) AS SemDataFechamento,
       MIN(DEFINITIVO + 0) AS DefinitivoMin, MAX(DEFINITIVO + 0) AS DefinitivoMax,
       MIN(STATUSTURNO) AS StatusMin, MAX(STATUSTURNO) AS StatusMax
FROM dbo.TURNOS WHERE ID_FILIAL = @Filial AND DATATURNO >= @Inicio AND DATATURNO < DATEADD(day, 1, @Fim);
