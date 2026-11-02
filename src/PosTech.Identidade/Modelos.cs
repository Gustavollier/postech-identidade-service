using System.Text.RegularExpressions;

namespace PosTech.Identidade;

/// <summary>
/// Os cargos, com os mesmos valores numéricos da Fase 3: a coluna
/// <c>Funcionario.Cargo</c> guarda o número, e os dados migrados do monólito
/// precisam continuar significando a mesma coisa.
/// </summary>
public enum ECargoFuncionario
{
    Mecanico = 0,
    Recepcionista = 1,
    Gerente = 2,
    Estoquista = 3,
    Eletricista = 4,
    Lavador = 5,
    Supervisor = 6,
}

public sealed class Cliente
{
    public int Id { get; set; }

    /// <summary>Só dígitos. Exatamente um entre CPF e CNPJ — o CHECK do banco garante.</summary>
    public string? CPF { get; set; }

    public string? CNPJ { get; set; }

    public string NomeCompleto { get; set; } = string.Empty;

    public string? Telefone { get; set; }

    public string? Email { get; set; }

    public bool Ativo { get; set; } = true;

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public string Documento => CPF ?? CNPJ ?? string.Empty;
}

public sealed class Veiculo
{
    public int Id { get; set; }

    public int ClienteId { get; set; }

    public string Marca { get; set; } = string.Empty;

    public string Modelo { get; set; } = string.Empty;

    public string Placa { get; set; } = string.Empty;

    public string? Cor { get; set; }

    public int? AnoModelo { get; set; }

    public int? AnoFabricacao { get; set; }

    public int? KmEntrada { get; set; }

    public bool Ativo { get; set; } = true;

    /// <summary>
    /// O texto que o OS Service guarda na ordem e que viaja no
    /// <c>OrdemAberta</c>: é como o mecânico reconhece o carro na fila.
    /// </summary>
    public string Descricao => $"{Marca} {Modelo} {Placa}".Trim();
}

public sealed class Funcionario
{
    public int Id { get; set; }

    public string Nome { get; set; } = string.Empty;

    public string? Contato { get; set; }

    public string CPF { get; set; } = string.Empty;

    public ECargoFuncionario Cargo { get; set; }

    public decimal ValorHora { get; set; }

    public bool Ativo { get; set; } = true;
}

/// <summary>A credencial do funcionário, separada do cadastro como na Fase 3.</summary>
public sealed class Seguranca
{
    public int Id { get; set; }

    public int FuncionarioId { get; set; }

    public string SenhaHash { get; set; } = string.Empty;

    public int TentativasFalhas { get; set; }

    public DateTime? BloqueadoAte { get; set; }

    public DateTime AtualizadoEm { get; set; }
}

/// <summary>
/// Placa no padrão antigo (ABC1234) ou Mercosul (ABC1D23), guardada sem
/// hífen e em maiúsculas — é o que torna o índice único confiável: "abc-1234"
/// e "ABC1234" são a mesma placa.
/// </summary>
public static partial class Placa
{
    [GeneratedRegex("^[A-Z]{3}[0-9][A-Z0-9][0-9]{2}$")]
    private static partial Regex Formato();

    public static string Normalizar(string? entrada)
    {
        var valor = new string((entrada ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

        if (!Formato().IsMatch(valor))
            throw new ArgumentException($"Placa inválida: '{entrada}'. Use ABC1234 ou ABC1D23.", nameof(entrada));

        return valor;
    }
}

/// <summary>Uma página de resultado, com o total para a tela montar a navegação.</summary>
public sealed record Pagina<T>(IReadOnlyList<T> Itens, int PaginaAtual, int TamanhoDaPagina, int Total)
{
    /// <summary>
    /// Arredonda para cima. A Fase 3 dividia por 10 fixo: 105 clientes davam
    /// 10 páginas e os 5 últimos nunca apareciam.
    /// </summary>
    public int TotalDePaginas => TamanhoDaPagina <= 0 ? 0 : (Total + TamanhoDaPagina - 1) / TamanhoDaPagina;

    public static (int Pagina, int Tamanho) Normalizar(int pagina, int tamanho) =>
        (Math.Max(1, pagina), Math.Clamp(tamanho, 1, 100));
}
