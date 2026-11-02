namespace PosTech.Identidade;

/// <summary>
/// CPF validado pelos dígitos verificadores.
///
/// Esta é a cópia que vale. Na Fase 3 a mesma regra existia em dois lugares —
/// no domínio do monólito e portada na Auth Function — porque a Function
/// precisava validar antes de consultar e não podia depender do assembly da
/// API. Agora os dois são o mesmo contexto: a Function é parte do Identidade
/// (ADR-0007) e as duas usam esta classe.
/// </summary>
public sealed class Cpf : IEquatable<Cpf>
{
    public const int Digitos = 11;

    private Cpf(string valor) => Valor = valor;

    /// <summary>Só os dígitos, sem pontuação.</summary>
    public string Valor { get; }

    public static Cpf Criar(string? entrada)
    {
        if (!TentarCriar(entrada, out var cpf, out var erro))
            throw new ArgumentException(erro, nameof(entrada));

        return cpf!;
    }

    /// <summary>
    /// Variante sem exceção. A Function usa esta para devolver 400 com a
    /// mensagem, em vez de capturar exceção no caminho feliz do erro.
    /// </summary>
    public static bool TentarCriar(string? entrada, out Cpf? cpf, out string? erro)
    {
        cpf = null;

        if (string.IsNullOrWhiteSpace(entrada))
        {
            erro = "CPF não pode ser vazio.";
            return false;
        }

        var limpo = SomenteDigitos(entrada);

        if (limpo.Length != Digitos)
        {
            erro = "CPF deve conter 11 dígitos.";
            return false;
        }

        // 111.111.111-11 passa nos dígitos verificadores, e é o CPF que todo
        // mundo digita para testar. Precisa de uma regra própria.
        if (limpo.Distinct().Count() == 1)
        {
            erro = "CPF inválido.";
            return false;
        }

        if (!DigitosConferem(limpo))
        {
            erro = "CPF inválido.";
            return false;
        }

        cpf = new Cpf(limpo);
        erro = null;
        return true;
    }

    /// <summary>Formatado para leitura: 123.456.789-09.</summary>
    public string Formatado() =>
        $"{Valor[..3]}.{Valor[3..6]}.{Valor[6..9]}-{Valor[9..]}";

    private static bool DigitosConferem(string cpf) =>
        cpf[9] - '0' == CalcularDigito(cpf, 10, 9) &&
        cpf[10] - '0' == CalcularDigito(cpf, 11, 10);

    private static int CalcularDigito(string cpf, int pesoInicial, int quantidade)
    {
        var soma = 0;

        for (var i = 0; i < quantidade; i++)
            soma += (cpf[i] - '0') * (pesoInicial - i);

        var resto = soma % 11;

        return resto < 2 ? 0 : 11 - resto;
    }

    internal static string SomenteDigitos(string valor) =>
        new([.. valor.Where(char.IsDigit)]);

    public bool Equals(Cpf? outro) => outro is not null && outro.Valor == Valor;

    public override bool Equals(object? obj) => Equals(obj as Cpf);

    public override int GetHashCode() => Valor.GetHashCode();

    public override string ToString() => Valor;

    public static implicit operator string(Cpf cpf) => cpf.Valor;
}

/// <summary>
/// CNPJ validado pelos dígitos verificadores. Cliente pessoa jurídica usa este
/// no lugar do CPF.
/// </summary>
public sealed class Cnpj : IEquatable<Cnpj>
{
    public const int Digitos = 14;

    private static readonly int[] PesosPrimeiro = [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];
    private static readonly int[] PesosSegundo = [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];

    private Cnpj(string valor) => Valor = valor;

    public string Valor { get; }

    public static Cnpj Criar(string? entrada)
    {
        if (!TentarCriar(entrada, out var cnpj, out var erro))
            throw new ArgumentException(erro, nameof(entrada));

        return cnpj!;
    }

    public static bool TentarCriar(string? entrada, out Cnpj? cnpj, out string? erro)
    {
        cnpj = null;

        if (string.IsNullOrWhiteSpace(entrada))
        {
            erro = "CNPJ não pode ser vazio.";
            return false;
        }

        var limpo = Cpf.SomenteDigitos(entrada);

        if (limpo.Length != Digitos)
        {
            erro = "CNPJ deve conter 14 dígitos.";
            return false;
        }

        if (limpo.Distinct().Count() == 1 || !DigitosConferem(limpo))
        {
            erro = "CNPJ inválido.";
            return false;
        }

        cnpj = new Cnpj(limpo);
        erro = null;
        return true;
    }

    public string Formatado() =>
        $"{Valor[..2]}.{Valor[2..5]}.{Valor[5..8]}/{Valor[8..12]}-{Valor[12..]}";

    private static bool DigitosConferem(string cnpj) =>
        cnpj[12] - '0' == CalcularDigito(cnpj, PesosPrimeiro) &&
        cnpj[13] - '0' == CalcularDigito(cnpj, PesosSegundo);

    private static int CalcularDigito(string cnpj, int[] pesos)
    {
        var soma = 0;

        for (var i = 0; i < pesos.Length; i++)
            soma += (cnpj[i] - '0') * pesos[i];

        var resto = soma % 11;

        return resto < 2 ? 0 : 11 - resto;
    }

    public bool Equals(Cnpj? outro) => outro is not null && outro.Valor == Valor;

    public override bool Equals(object? obj) => Equals(obj as Cnpj);

    public override int GetHashCode() => Valor.GetHashCode();

    public override string ToString() => Valor;
}
