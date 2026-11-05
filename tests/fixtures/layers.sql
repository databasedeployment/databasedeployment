-- Test fixture: a layered warehouse layout with table changes that carry deployment risk.
--
-- Usage:
--   sqlcmd -S <server> -E -C -b -i layers.sql -v DbName="EDWSTG" Variant="DEV"
--   sqlcmd -S <server> -E -C -b -i layers.sql -v DbName="EDWSTG" Variant="PROD"
--
-- DEV  = source ("what it should be")
-- PROD = target ("what is live now")
--
-- FactSale  : has rows in PROD -> risky changes must block the deployment
-- DimEmpty  : empty in PROD     -> the same changes must not block
-- Telling these two cases apart is the whole point of the risk analysis.

IF DB_ID('$(DbName)') IS NOT NULL
BEGIN
    ALTER DATABASE [$(DbName)] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [$(DbName)];
END
GO
CREATE DATABASE [$(DbName)];
GO
USE [$(DbName)];
GO

DECLARE @variant sysname = '$(Variant)';

IF @variant = 'DEV'
BEGIN
    EXEC(N'
    CREATE TABLE dbo.FactSale (
        SaleId      bigint          IDENTITY(1,1) NOT NULL,
        Code        varchar(50)     NOT NULL,          -- PROD: varchar(20)   -> widening
        Description nvarchar(100)   NULL,              -- PROD: nvarchar(500) -> NARROWING
        Amount      decimal(18,2)   NOT NULL,          -- PROD: decimal(18,4) -> LOSES PRECISION
        Quantity    int             NOT NULL,          -- PROD: smallint      -> widening
        Category    nvarchar(50)    NOT NULL,          -- PROD: allows NULL -> NOT NULL
        TenantId    int             NOT NULL,          -- PROD: missing, and no DEFAULT
        Note        nvarchar(200)   NULL,              -- PROD: missing, nullable -> safe
        CONSTRAINT PK_FactSale PRIMARY KEY CLUSTERED (SaleId));');

    EXEC(N'
    CREATE TABLE dbo.DimEmpty (
        DimId       int             IDENTITY(1,1) NOT NULL,
        Code        varchar(50)     NOT NULL,
        Description nvarchar(100)   NULL,
        Amount      decimal(18,2)   NOT NULL,
        Quantity    int             NOT NULL,
        Category    nvarchar(50)    NOT NULL,
        TenantId    int             NOT NULL,
        Note        nvarchar(200)   NULL,
        CONSTRAINT PK_DimEmpty PRIMARY KEY CLUSTERED (DimId));');
END
ELSE
BEGIN
    EXEC(N'
    CREATE TABLE dbo.FactSale (
        SaleId       bigint          IDENTITY(1,1) NOT NULL,
        Code         varchar(20)     NOT NULL,
        Description  nvarchar(500)   NULL,
        Amount       decimal(18,4)   NOT NULL,
        Quantity     smallint        NOT NULL,
        Category     nvarchar(50)    NULL,
        ObsoleteCode varchar(10)     NULL,             -- DEV: missing -> WILL BE DROPPED
        CONSTRAINT PK_FactSale PRIMARY KEY CLUSTERED (SaleId));');

    EXEC(N'
    CREATE TABLE dbo.DimEmpty (
        DimId        int             IDENTITY(1,1) NOT NULL,
        Code         varchar(20)     NOT NULL,
        Description  nvarchar(500)   NULL,
        Amount       decimal(18,4)   NOT NULL,
        Quantity     smallint        NOT NULL,
        Category     nvarchar(50)    NULL,
        ObsoleteCode varchar(10)     NULL,
        CONSTRAINT PK_DimEmpty PRIMARY KEY CLUSTERED (DimId));');
END
GO

-- Identical on both sides: must not produce a difference.
CREATE TABLE dbo.DimCalendar
(
    DateKey int          NOT NULL,
    [Year]  smallint     NOT NULL,
    [Month] tinyint      NOT NULL,
    DayName nvarchar(20) NOT NULL,
    CONSTRAINT PK_DimCalendar PRIMARY KEY CLUSTERED (DateKey)
);
GO

CREATE INDEX IX_FactSale_Code ON dbo.FactSale (Code);
GO

CREATE PROCEDURE dbo.LoadFactSale
AS
BEGIN
    SET NOCOUNT ON;
    SELECT COUNT(*) AS RowCountValue FROM dbo.FactSale;
END
GO

CREATE TRIGGER dbo.trg_FactSale_Guard ON dbo.FactSale AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
END
GO

-- PROD side: put rows into FactSale, leave DimEmpty empty and disable the trigger.
DECLARE @variant2 sysname = '$(Variant)';

-- Dynamic SQL is required. A batch cannot reference columns that do not exist on the DEV side
-- at compile time, and putting them inside an IF block does not prevent compilation.
IF @variant2 = 'PROD'
BEGIN
    EXEC(N'
    INSERT dbo.FactSale (Code, Description, Amount, Quantity, Category, ObsoleteCode)
    SELECT TOP (5000)
           LEFT(CONVERT(varchar(36), NEWID()), 20),
           N''row'',
           123.4567,
           1,
           N''general'',
           NULL
    FROM sys.all_columns AS a CROSS JOIN sys.all_columns AS b;

    DISABLE TRIGGER dbo.trg_FactSale_Guard ON dbo.FactSale;');
END
GO

SELECT DB_NAME() AS DatabaseName,
       (SELECT COUNT(*) FROM dbo.FactSale) AS FactSaleRows,
       (SELECT COUNT(*) FROM dbo.DimEmpty) AS DimEmptyRows;
GO
