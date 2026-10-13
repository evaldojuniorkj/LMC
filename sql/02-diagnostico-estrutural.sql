-- Diagnostico de leitura. Resultados sao candidatos a revisao, nao laudo fiscal.
-- Parametros: escolher filial e, opcionalmente, data final inclusiva.
-- Nao infere ausencia de livros depois da data mais recente desta copia.
SET NOCOUNT ON;
SET LOCK_TIMEOUT 5000;

DECLARE @Filial smallint = 17581;
DECLARE @DataFinal date = NULL;
DECLARE @Dias int = 30;

IF @Dias < 1 OR @Dias > 366
    THROW 50001, 'Escolha um intervalo entre 1 e 366 dias.', 1;

IF @DataFinal IS NULL
    SELECT @DataFinal = CAST(MAX(DTACONTA) AS date)
    FROM dbo.LMC WHERE ID_FILIAL = @Filial;

IF @DataFinal IS NULL
BEGIN
    SELECT 'Sem livros datados para a filial; nenhuma regra foi avaliada.' AS Resultado;
    RETURN;
END;

DECLARE @Inicio date = DATEADD(day, 1 - @Dias, @DataFinal);
DECLARE @FimExclusivo date = DATEADD(day, 1, @DataFinal);

SELECT @Filial AS Filial, @Inicio AS InicioInclusivo, @DataFinal AS FimInclusivo,
       COUNT_BIG(*) AS LivrosNoPeriodo
FROM dbo.LMC
WHERE ID_FILIAL = @Filial AND DTACONTA >= @Inicio AND DTACONTA < @FimExclusivo;

-- Revisar repeticoes conforme a regra operacional e a configuracao do Xpert.
SELECT 'LIVRO_REPETIDO' AS Regra, ID_FILIAL, ID_PRODUTOS,
       CAST(DTACONTA AS date) AS Data, COUNT_BIG(*) AS Quantidade
FROM dbo.LMC
WHERE ID_FILIAL = @Filial AND DTACONTA >= @Inicio AND DTACONTA < @FimExclusivo
GROUP BY ID_FILIAL, ID_PRODUTOS, CAST(DTACONTA AS date)
HAVING COUNT_BIG(*) > 1
ORDER BY Data, ID_PRODUTOS;

-- Ausencia de filhos pode representar livro incompleto ou caso excepcional.
SELECT l.ID_FILIAL, l.ID_LMC, l.ID_PRODUTOS, l.DTACONTA,
       CASE WHEN NOT EXISTS (
           SELECT 1 FROM dbo.LMCBICOS b
           WHERE b.ID_FILIAL = l.ID_FILIAL AND b.ID_LMC = l.ID_LMC
       ) THEN 1 ELSE 0 END AS SemBicos,
       CASE WHEN NOT EXISTS (
           SELECT 1 FROM dbo.LMCTANQUES t
           WHERE t.ID_FILIAL = l.ID_FILIAL AND t.ID_LMC = l.ID_LMC
       ) THEN 1 ELSE 0 END AS SemTanques
FROM dbo.LMC l
WHERE l.ID_FILIAL = @Filial AND l.DTACONTA >= @Inicio AND l.DTACONTA < @FimExclusivo
  AND (NOT EXISTS (SELECT 1 FROM dbo.LMCBICOS b WHERE b.ID_FILIAL = l.ID_FILIAL AND b.ID_LMC = l.ID_LMC)
       OR NOT EXISTS (SELECT 1 FROM dbo.LMCTANQUES t WHERE t.ID_FILIAL = l.ID_FILIAL AND t.ID_LMC = l.ID_LMC))
ORDER BY l.DTACONTA, l.ID_PRODUTOS;

-- Nao substitui NULL por zero nem pressupoe a quantidade de digitos do contador.
SELECT l.ID_FILIAL, l.ID_LMC, l.ID_PRODUTOS, l.DTACONTA, b.ID_BICOS,
       b.ENCERRANTEABERTURA, b.ENCERRANTEFECHAMENTO, b.AFERICAO,
       CASE WHEN b.ENCERRANTEABERTURA IS NULL OR b.ENCERRANTEFECHAMENTO IS NULL
            THEN 'LEITURA_AUSENTE'
            ELSE 'ENCERRANTE_REDUZIU_VERIFICAR_INTERVENCAO_OU_VIRADA' END AS Regra
FROM dbo.LMC l
JOIN dbo.LMCBICOS b ON b.ID_FILIAL = l.ID_FILIAL AND b.ID_LMC = l.ID_LMC
WHERE l.ID_FILIAL = @Filial AND l.DTACONTA >= @Inicio AND l.DTACONTA < @FimExclusivo
  AND (b.ENCERRANTEABERTURA IS NULL OR b.ENCERRANTEFECHAMENTO IS NULL
       OR b.ENCERRANTEFECHAMENTO < b.ENCERRANTEABERTURA)
ORDER BY l.DTACONTA, l.ID_PRODUTOS, b.ID_BICOS;

-- Livros sem data ficam fora de qualquer filtro por periodo: contar separadamente.
SELECT 'LIVROS_SEM_DATA_EM_TODA_A_FILIAL' AS Regra, COUNT_BIG(*) AS Quantidade
FROM dbo.LMC WHERE ID_FILIAL = @Filial AND DTACONTA IS NULL;
