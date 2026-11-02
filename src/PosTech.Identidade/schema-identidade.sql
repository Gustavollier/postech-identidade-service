-- ---------------------------------------------------------------------------
-- Schema do identidade-db.
--
-- É o schema de Cliente, Veiculo, Funcionario e Seguranca da Fase 3, movido
-- para o banco do serviço dono deles — e com as restrições que o documento de
-- entrega da Fase 3 listou como recomendação para um próximo incremento.
-- A Fase 4 é esse incremento.
--
-- Idempotente, no mesmo formato do schema-azure.sql: pode ser reaplicado sobre
-- um ambiente já provisionado.
-- ---------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Cliente')
BEGIN
    CREATE TABLE Cliente (
        Id INT PRIMARY KEY IDENTITY(1,1),
        CPF NVARCHAR(11) NULL,
        CNPJ NVARCHAR(14) NULL,
        NomeCompleto NVARCHAR(150) NOT NULL,
        Telefone NVARCHAR(20) NULL,
        Email NVARCHAR(150) NULL,
        Ativo BIT NOT NULL DEFAULT 1,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        UpdatedAt DATETIME2 NULL,

        -- Exatamente um dos dois documentos. Sem esta regra, os dois eram
        -- opcionais e um cliente podia existir sem documento nenhum — e a
        -- autenticação por CPF depende de o documento identificar alguém.
        CONSTRAINT CK_Cliente_UmDocumento CHECK (
            (CPF IS NOT NULL AND CNPJ IS NULL) OR
            (CPF IS NULL AND CNPJ IS NOT NULL)
        )
    );
END;
GO

-- Os índices únicos são filtrados porque o documento é opcional (um cliente tem
-- CPF ou CNPJ, nunca os dois) e no SQL Server um UNIQUE comum trataria vários
-- NULL como duplicata.
--
-- Isto fecha a lacuna mais séria da Fase 3: a Auth Function faz
-- SELECT TOP 1 ... WHERE CPF = @Cpf. Com CPF duplicado ela não falhava — ela
-- autenticava um dos dois clientes, sem critério, e ninguém ficava sabendo.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Cliente_CPF')
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX UX_Cliente_CPF
        ON Cliente (CPF) WHERE CPF IS NOT NULL;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Cliente_CNPJ')
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX UX_Cliente_CNPJ
        ON Cliente (CNPJ) WHERE CNPJ IS NOT NULL;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Funcionario')
BEGIN
    CREATE TABLE Funcionario (
        Id INT PRIMARY KEY IDENTITY(1,1),
        Nome NVARCHAR(100) NOT NULL,
        Contato NVARCHAR(50) NULL,
        CPF NVARCHAR(11) NOT NULL,
        Cargo INT NOT NULL,
        ValorHora DECIMAL(10, 2) NOT NULL,
        -- Desativação em vez de exclusão, como na Fase 3: Funcionario é
        -- referenciado por Seguranca e, nos outros serviços, por ordem e itens.
        Ativo BIT NOT NULL DEFAULT 1
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Funcionario_CPF')
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX UX_Funcionario_CPF ON Funcionario (CPF);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Seguranca')
BEGIN
    CREATE TABLE Seguranca (
        Id INT PRIMARY KEY IDENTITY(1,1),
        FuncionarioId INT NOT NULL,
        SenhaHash NVARCHAR(255) NOT NULL,
        TentativasFalhas INT NOT NULL DEFAULT 0,
        BloqueadoAte DATETIME2 NULL,
        AtualizadoEm DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),

        CONSTRAINT FK_Seguranca_Funcionario FOREIGN KEY (FuncionarioId) REFERENCES Funcionario (Id),
        CONSTRAINT UQ_Seguranca_FuncionarioId UNIQUE (FuncionarioId)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Veiculo')
BEGIN
    CREATE TABLE Veiculo (
        Id INT PRIMARY KEY IDENTITY(1,1),
        ClienteId INT NOT NULL,
        Marca NVARCHAR(50) NOT NULL,
        Modelo NVARCHAR(50) NOT NULL,
        Placa NVARCHAR(10) NOT NULL,
        Cor NVARCHAR(30) NULL,
        AnoModelo INT NULL,
        AnoFabricacao INT NULL,
        KmEntrada INT NULL,
        Ativo BIT NOT NULL DEFAULT 1,

        CONSTRAINT FK_Veiculo_Cliente FOREIGN KEY (ClienteId) REFERENCES Cliente (Id)
    );
END;
GO

-- Placa é única entre veículos ativos. Filtrado por Ativo porque um veículo
-- desativado pode ter a placa reaproveitada por outro dono depois da venda.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Veiculo_Placa')
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX UX_Veiculo_Placa
        ON Veiculo (Placa) WHERE Ativo = 1;
END;
GO

