-- Explora hipoteses usando os valores existentes. Nao calcula conformidade fiscal.
-- 0.02 L e uma tolerancia exploratoria, nao normativa.
SET NOCOUNT ON;
SET LOCK_TIMEOUT 5000;

DECLARE @Filial smallint = 17581;
DECLARE @Fim date = (SELECT CAST(MAX(DTACONTA) AS date) FROM dbo.LMC WHERE ID_FILIAL = @Filial);
IF @Fim IS NULL
BEGIN
    SELECT 'Sem livros datados; nenhuma comparacao executada.' AS Resultado;
    RETURN;
END;
DECLARE @Inicio date = DATEADD(day, -29, @Fim);
DECLARE @Tolerancia float = 0.02;

SELECT @Filial AS Filial, @Inicio AS InicioInclusivo, @Fim AS FimInclusivo,
       @Tolerancia AS ToleranciaExploratoriaLitros;

SELECT COUNT_BIG(*) AS RegistrosDeBicos,
       SUM(CASE WHEN b.VENDAS IS NULL THEN 1 ELSE 0 END) AS VendasNulas,
       SUM(CASE WHEN b.VENDAS = 0 THEN 1 ELSE 0 END) AS VendasZeradas,
       SUM(CASE WHEN b.AFERICAO IS NULL THEN 1 ELSE 0 END) AS AfericoesNulas,
       SUM(CASE WHEN b.AFERICAO > 0 THEN 1 ELSE 0 END) AS AfericoesPositivas,
       SUM(CASE WHEN ABS(b.VENDAS - (b.ENCERRANTEFECHAMENTO - b.ENCERRANTEABERTURA - b.AFERICAO)) <= @Tolerancia
                THEN 1 ELSE 0 END) AS CompativelComLeituraIntegral,
       SUM(CASE WHEN ABS(b.VENDAS - (ROUND(b.ENCERRANTEFECHAMENTO, 0, 1) - ROUND(b.ENCERRANTEABERTURA, 0, 1) - b.AFERICAO)) <= @Tolerancia
                THEN 1 ELSE 0 END) AS CompativelComEncerrantesTruncados
FROM dbo.LMC l
JOIN dbo.LMCBICOS b ON b.ID_LMC = l.ID_LMC AND b.ID_FILIAL = l.ID_FILIAL
WHERE l.ID_FILIAL = @Filial AND l.DTACONTA >= @Inicio AND l.DTACONTA < DATEADD(day, 1, @Fim);

SELECT COUNT_BIG(*) AS RegistrosDeTanques,
       SUM(CASE WHEN t.QTDETANQUE IS NULL THEN 1 ELSE 0 END) AS QtdeNula,
       SUM(CASE WHEN t.QTDECONCILIACAO IS NULL THEN 1 ELSE 0 END) AS ConciliacaoNula,
       SUM(CASE WHEN t.QTDECONCILIACAO = 0 THEN 1 ELSE 0 END) AS ConciliacaoZero
FROM dbo.LMC l
JOIN dbo.LMCTANQUES t ON t.ID_LMC = l.ID_LMC AND t.ID_FILIAL = l.ID_FILIAL
WHERE l.ID_FILIAL = @Filial AND l.DTACONTA >= @Inicio AND l.DTACONTA < DATEADD(day, 1, @Fim);
