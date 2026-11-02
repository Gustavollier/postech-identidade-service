using System.Data;
using System.Reflection;
using Dapper;
using Microsoft.Data.SqlClient;

namespace PosTech.Identidade;

/// <summary>Já existe um registro com este valor num campo único.</summary>
public sealed class RegistroDuplicadoException(string campo, string valor)
    : Exception($"Já existe um cadastro com {campo} '{valor}'.")
{
    public string Campo { get; } = campo;
}

public sealed class RegistroNaoEncontradoException(string entidade, int id)
    : Exception($"{entidade} {id} não encontrado.")
{
    public string Entidade { get; } = entidade;
}

/// <summary>Abre conexões com o <c>identidade-db</c>.</summary>
public sealed class FabricaDeConexao(string connectionString)
{
    public async Task<SqlConnection> AbrirAsync(CancellationToken cancellationToken = default)
    {
        var conexao = new SqlConnection(connectionString);
        await conexao.OpenAsync(cancellationToken).ConfigureAwait(false);
        return conexao;
    }
}

/// <summary>
/// Aplica o <c>schema-identidade.sql</c>. O script é idempotente, então rodar
/// na partida de toda réplica é seguro — e é o que tira do caminho o "esqueceram
/// de rodar o script" que a Fase 3 tinha em cada ambiente novo.
/// </summary>
public static class InstaladorDoSchema
{
    public static string Script()
    {
        using var recurso = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("PosTech.Identidade.schema-identidade.sql")
            ?? throw new InvalidOperationException("schema-identidade.sql não está embutido no assembly.");

        using var leitor = new StreamReader(recurso);
        return leitor.ReadToEnd();
    }

    public static async Task AplicarAsync(FabricaDeConexao fabrica, CancellationToken cancellationToken = default)
    {
        await using var conexao = await fabrica.AbrirAsync(cancellationToken).ConfigureAwait(false);

        // GO é separador do sqlcmd, não do T-SQL: o SqlClient não o entende.
        var lotes = Script()
            .Replace("\r\n", "\n")
            .Split("\nGO", StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.Split('\n').All(linha => linha.TrimStart().StartsWith("--") || linha.Trim().Length == 0));

        foreach (var lote in lotes)
            await conexao.ExecuteAsync(new CommandDefinition(lote, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}

/// <summary>Os códigos de erro do SQL Server que viram regra de negócio.</summary>
internal static class ErrosDoSql
{
    /// <summary>2601: índice único; 2627: constraint UNIQUE ou PK.</summary>
    public static bool Duplicado(SqlException e) => e.Number is 2601 or 2627;

    /// <summary>547: violação de FOREIGN KEY ou CHECK.</summary>
    public static bool Restricao(SqlException e) => e.Number == 547;
}

public interface IClienteRepositorio
{
    Task<int> InserirAsync(Cliente cliente, CancellationToken cancellationToken = default);

    Task<Cliente?> ObterAsync(int id, CancellationToken cancellationToken = default);

    Task<Cliente?> ObterPorDocumentoAsync(string documento, CancellationToken cancellationToken = default);

    Task<Pagina<Cliente>> ListarAsync(int pagina, int tamanho, CancellationToken cancellationToken = default);

    Task<bool> AtualizarAsync(Cliente cliente, CancellationToken cancellationToken = default);

    Task<bool> DesativarAsync(int id, CancellationToken cancellationToken = default);
}

public sealed class ClienteRepositorio(FabricaDeConexao fabrica) : IClienteRepositorio
{
    private const string Colunas = "Id, CPF, CNPJ, NomeCompleto, Telefone, Email, Ativo, CreatedAt, UpdatedAt";

    public async Task<int> InserirAsync(Cliente cliente, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO Cliente (CPF, CNPJ, NomeCompleto, Telefone, Email)
            OUTPUT INSERTED.Id, INSERTED.CreatedAt
            VALUES (@CPF, @CNPJ, @NomeCompleto, @Telefone, @Email);
            """;

        await using var conexao = await fabrica.AbrirAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var criado = await conexao.QuerySingleAsync<(int Id, DateTime CreatedAt)>(
                new CommandDefinition(sql, cliente, cancellationToken: cancellationToken)).ConfigureAwait(false);

            cliente.Id = criado.Id;
            cliente.CreatedAt = criado.CreatedAt;
            return criado.Id;
        }
        catch (SqlException e) when (ErrosDoSql.Duplicado(e))
        {
            // O índice único é a garantia; a checagem antes, no serviço, é só
            // para a mensagem sair boa no caso comum. Na corrida, quem perde
            // cai aqui.
            throw new RegistroDuplicadoException(cliente.CPF is not null ? "CPF" : "CNPJ", cliente.Documento);
        }
    }

    public async Task<Cliente?> ObterAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var conexao = await fabrica.AbrirAsync(cancellationToken).ConfigureAwait(false);

        return await conexao.QuerySingleOrDefaultAsync<Cliente>(new CommandDefinition(
            $"SELECT {Colunas} FROM Cliente WHERE Id = @id AND Ativo = 1", new { id },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<Cliente?> ObterPorDocumentoAsync(string documento, CancellationToken cancellationToken = default)
    {
        await using var conexao = await fabrica.AbrirAsync(cancellationToken).ConfigureAwait(false);

        return await conexao.QuerySingleOrDefaultAsync<Cliente>(new CommandDefinition(
            $"SELECT {Colunas} FROM Cliente WHERE (CPF = @documento OR CNPJ = @documento) AND Ativo = 1",
            new { documento }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<Pagina<Cliente>> ListarAsync(int pagina, int tamanho, CancellationToken cancellationToken = default)
    {
        (pagina, tamanho) = Pagina<Cliente>.Normalizar(pagina, tamanho);

        // Uma consulta só, com a contagem na janela: a Fase 3 fazia duas, e a
        // contagem podia mudar entre uma e outra.
        var sql = $"""
            SELECT COUNT(*) OVER () AS Total, {Colunas}
            FROM Cliente
            WHERE Ativo = 1
            ORDER BY Id
            OFFSET @pular ROWS FETCH NEXT @tamanho ROWS ONLY;
            """;

        await using var conexao = await fabrica.AbrirAsync(cancellationToken).ConfigureAwait(false);

        var total = 0;
        var itens = await conexao.QueryAsync<int, Cliente, Cliente>(
            new CommandDefinition(sql, new { pular = (pagina - 1) * tamanho, tamanho }, cancellationToken: cancellationToken),
            (contagem, cliente) =>
            {
                total = contagem;
                return cliente;
            },
            splitOn: "Id").ConfigureAwait(false);

        var lista = itens.ToList();

        // Página além do fim não traz linha nenhuma, e a contagem vem junto da
        // linha. Sem isto, pedir a página 50 diria que há zero clientes.
        if (lista.Count == 0 && pagina > 1)
        {
            total = await conexao.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM Cliente WHERE Ativo = 1", cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        return new Pagina<Cliente>(lista, pagina, tamanho, total);
    }

    public async Task<bool> AtualizarAsync(Cliente cliente, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE Cliente
            SET NomeCompleto = @NomeCompleto, Telefone = @Telefone, Email = @Email, UpdatedAt = SYSUTCDATETIME()
            WHERE Id = @Id AND Ativo = 1;
            """;

        await using var conexao = await fabrica.AbrirAsync(cancellationToken).ConfigureAwait(false);

        return await conexao.ExecuteAsync(new CommandDefinition(sql, cliente, cancellationToken: cancellationToken))
            .ConfigureAwait(false) == 1;
    }

    /// <summary>
    /// Desativa em vez de apagar: o cliente é referenciado por ordens de
    /// serviço em outro banco, e apagar deixaria ordem apontando para o nada.
    /// </summary>
    public async Task<bool> DesativarAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var conexao = await fabrica.AbrirAsync(cancellationToken).ConfigureAwait(false);

        return await conexao.ExecuteAsync(new CommandDefinition(
            "UPDATE Cliente SET Ativo = 0, UpdatedAt = SYSUTCDATETIME() WHERE Id = @id AND Ativo = 1",
            new { id }, cancellationToken: cancellationToken)).ConfigureAwait(false) == 1;
    }
}

public interface IVeiculoRepositorio
{
    Task<int> InserirAsync(Veiculo veiculo, CancellationToken cancellationToken = default);

    Task<Veiculo?> ObterAsync(int id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Veiculo>> ListarDoClienteAsync(int clienteId, CancellationToken cancellationToken = default);

    Task<bool> AtualizarAsync(Veiculo veiculo, CancellationToken cancellationToken = default);

    Task<bool> DesativarAsync(int id, CancellationToken cancellationToken = default);
}

public sealed class VeiculoRepositorio(FabricaDeConexao fabrica) : IVeiculoRepositorio
{
    private const string Colunas = "Id, ClienteId, Marca, Modelo, Placa, Cor, AnoModelo, AnoFabricacao, KmEntrada, Ativo";

    public async Task<int> InserirAsync(Veiculo veiculo, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO Veiculo (ClienteId, Marca, Modelo, Placa, Cor, AnoModelo, AnoFabricacao, KmEntrada)
            OUTPUT INSERTED.Id
            VALUES (@ClienteId, @Marca, @Modelo, @Placa, @Cor, @AnoModelo, @AnoFabricacao, @KmEntrada);
            """;

        await using var conexao = await fabrica.AbrirAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            veiculo.Id = await conexao.ExecuteScalarAsync<int>(
                new CommandDefinition(sql, veiculo, cancellationToken: cancellationToken)).ConfigureAwait(false);

            return veiculo.Id;
        }
        catch (SqlException e) when (ErrosDoSql.Duplicado(e))
        {
            throw new RegistroDuplicadoException("placa", veiculo.Placa);
        }
        catch (SqlException e) when (ErrosDoSql.Restricao(e))
        {
            throw new RegistroNaoEncontradoException("Cliente", veiculo.ClienteId);
        }
    }

    public async Task<Veiculo?> ObterAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var conexao = await fabrica.AbrirAsync(cancellationToken).ConfigureAwait(false);

        return await conexao.QuerySingleOrDefaultAsync<Veiculo>(new CommandDefinition(
            $"SELECT {Colunas} FROM Veiculo WHERE Id = @id AND Ativo = 1", new { id },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Veiculo>> ListarDoClienteAsync(int clienteId, CancellationToken cancellationToken = default)
    {
        await using var conexao = await fabrica.AbrirAsync(cancellationToken).ConfigureAwait(false);

        var veiculos = await conexao.QueryAsync<Veiculo>(new CommandDefinition(
            $"SELECT {Colunas} FROM Veiculo WHERE ClienteId = @clienteId AND Ativo = 1 ORDER BY Id",
            new { clienteId }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return veiculos.ToList();
    }

    public async Task<bool> AtualizarAsync(Veiculo veiculo, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE Veiculo
            SET Marca = @Marca, Modelo = @Modelo, Cor = @Cor, AnoModelo = @AnoModelo,
                AnoFabricacao = @AnoFabricacao, KmEntrada = @KmEntrada
            WHERE Id = @Id AND Ativo = 1;
            """;

        await using var conexao = await fabrica.AbrirAsync(cancellationToken).ConfigureAwait(false);

        return await conexao.ExecuteAsync(new CommandDefinition(sql, veiculo, cancellationToken: cancellationToken))
            .ConfigureAwait(false) == 1;
    }

    /// <summary>
    /// Desativar libera a placa: o índice único só olha veículos ativos,
    /// porque um carro vendido aparece de novo com outro dono.
    /// </summary>
    public async Task<bool> DesativarAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var conexao = await fabrica.AbrirAsync(cancellationToken).ConfigureAwait(false);

        return await conexao.ExecuteAsync(new CommandDefinition(
            "UPDATE Veiculo SET Ativo = 0 WHERE Id = @id AND Ativo = 1", new { id },
            cancellationToken: cancellationToken)).ConfigureAwait(false) == 1;
    }
}

public interface IFuncionarioRepositorio
{
    /// <summary>Grava o funcionário e a credencial na mesma transação.</summary>
    Task<int> InserirAsync(Funcionario funcionario, string senhaHash, CancellationToken cancellationToken = default);

    Task<Funcionario?> ObterAsync(int id, CancellationToken cancellationToken = default);

    Task<Funcionario?> ObterPorCpfAsync(string cpf, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Funcionario>> ListarAsync(CancellationToken cancellationToken = default);

    Task<bool> AtualizarAsync(Funcionario funcionario, CancellationToken cancellationToken = default);

    Task<bool> DesativarAsync(int id, CancellationToken cancellationToken = default);

    Task<Seguranca?> ObterSegurancaAsync(int funcionarioId, CancellationToken cancellationToken = default);

    Task SalvarSegurancaAsync(Seguranca seguranca, CancellationToken cancellationToken = default);
}

public sealed class FuncionarioRepositorio(FabricaDeConexao fabrica) : IFuncionarioRepositorio
{
    private const string Colunas = "Id, Nome, Contato, CPF, Cargo, ValorHora, Ativo";

    public async Task<int> InserirAsync(Funcionario funcionario, string senhaHash, CancellationToken cancellationToken = default)
    {
        await using var conexao = await fabrica.AbrirAsync(cancellationToken).ConfigureAwait(false);
        await using var transacao = (SqlTransaction)await conexao.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            funcionario.Id = await conexao.ExecuteScalarAsync<int>(new CommandDefinition(
                """
                INSERT INTO Funcionario (Nome, Contato, CPF, Cargo, ValorHora)
                OUTPUT INSERTED.Id
                VALUES (@Nome, @Contato, @CPF, @Cargo, @ValorHora);
                """,
                new { funcionario.Nome, funcionario.Contato, funcionario.CPF, Cargo = (int)funcionario.Cargo, funcionario.ValorHora },
                transacao, cancellationToken: cancellationToken)).ConfigureAwait(false);

            // Funcionário sem credencial não consegue entrar nunca; credencial
            // sem funcionário não existe. As duas linhas nascem juntas ou não
            // nascem.
            await conexao.ExecuteAsync(new CommandDefinition(
                "INSERT INTO Seguranca (FuncionarioId, SenhaHash) VALUES (@Id, @senhaHash)",
                new { funcionario.Id, senhaHash }, transacao, cancellationToken: cancellationToken)).ConfigureAwait(false);

            await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);
            return funcionario.Id;
        }
        catch (SqlException e) when (ErrosDoSql.Duplicado(e))
        {
            await transacao.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw new RegistroDuplicadoException("CPF", funcionario.CPF);
        }
    }

    public async Task<Funcionario?> ObterAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var conexao = await fabrica.AbrirAsync(cancellationToken).ConfigureAwait(false);

        return await conexao.QuerySingleOrDefaultAsync<Funcionario>(new CommandDefinition(
            $"SELECT {Colunas} FROM Funcionario WHERE Id = @id AND Ativo = 1", new { id },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<Funcionario?> ObterPorCpfAsync(string cpf, CancellationToken cancellationToken = default)
    {
        await using var conexao = await fabrica.AbrirAsync(cancellationToken).ConfigureAwait(false);

        return await conexao.QuerySingleOrDefaultAsync<Funcionario>(new CommandDefinition(
            $"SELECT {Colunas} FROM Funcionario WHERE CPF = @cpf AND Ativo = 1", new { cpf },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Funcionario>> ListarAsync(CancellationToken cancellationToken = default)
    {
        await using var conexao = await fabrica.AbrirAsync(cancellationToken).ConfigureAwait(false);

        var funcionarios = await conexao.QueryAsync<Funcionario>(new CommandDefinition(
            $"SELECT {Colunas} FROM Funcionario WHERE Ativo = 1 ORDER BY Nome",
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return funcionarios.ToList();
    }

    public async Task<bool> AtualizarAsync(Funcionario funcionario, CancellationToken cancellationToken = default)
    {
        await using var conexao = await fabrica.AbrirAsync(cancellationToken).ConfigureAwait(false);

        return await conexao.ExecuteAsync(new CommandDefinition(
            """
            UPDATE Funcionario
            SET Nome = @Nome, Contato = @Contato, Cargo = @Cargo, ValorHora = @ValorHora
            WHERE Id = @Id AND Ativo = 1;
            """,
            new { funcionario.Id, funcionario.Nome, funcionario.Contato, Cargo = (int)funcionario.Cargo, funcionario.ValorHora },
            cancellationToken: cancellationToken)).ConfigureAwait(false) == 1;
    }

    public async Task<bool> DesativarAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var conexao = await fabrica.AbrirAsync(cancellationToken).ConfigureAwait(false);

        return await conexao.ExecuteAsync(new CommandDefinition(
            "UPDATE Funcionario SET Ativo = 0 WHERE Id = @id AND Ativo = 1", new { id },
            cancellationToken: cancellationToken)).ConfigureAwait(false) == 1;
    }

    public async Task<Seguranca?> ObterSegurancaAsync(int funcionarioId, CancellationToken cancellationToken = default)
    {
        await using var conexao = await fabrica.AbrirAsync(cancellationToken).ConfigureAwait(false);

        return await conexao.QuerySingleOrDefaultAsync<Seguranca>(new CommandDefinition(
            """
            SELECT Id, FuncionarioId, SenhaHash, TentativasFalhas, BloqueadoAte, AtualizadoEm
            FROM Seguranca WHERE FuncionarioId = @funcionarioId
            """,
            new { funcionarioId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task SalvarSegurancaAsync(Seguranca seguranca, CancellationToken cancellationToken = default)
    {
        await using var conexao = await fabrica.AbrirAsync(cancellationToken).ConfigureAwait(false);

        await conexao.ExecuteAsync(new CommandDefinition(
            """
            UPDATE Seguranca
            SET SenhaHash = @SenhaHash, TentativasFalhas = @TentativasFalhas,
                BloqueadoAte = @BloqueadoAte, AtualizadoEm = @AtualizadoEm
            WHERE FuncionarioId = @FuncionarioId;
            """,
            seguranca, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
