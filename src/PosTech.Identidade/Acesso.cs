namespace PosTech.Identidade;

/// <summary>
/// As regras de senha e de bloqueio do login de funcionário. Funções puras:
/// o relógio entra por parâmetro.
/// </summary>
public static class Acesso
{
    /// <summary>
    /// Tentativas erradas seguidas antes de bloquear. A Fase 3 tinha as
    /// colunas <c>TentativasFalhas</c> e <c>BloqueadoAte</c> e nunca as usou:
    /// o login aceitava tentativa infinita.
    /// </summary>
    public const int TentativasAntesDoBloqueio = 5;

    public static readonly TimeSpan DuracaoDoBloqueio = TimeSpan.FromMinutes(15);

    /// <summary>
    /// A mesma política da Fase 3, para quem já tem senha continuar podendo
    /// trocar pelas mesmas regras: oito caracteres, uma maiúscula, um número
    /// e um caractere especial.
    /// </summary>
    public static void ValidarSenhaNova(string? senha)
    {
        if (string.IsNullOrWhiteSpace(senha))
            throw new ArgumentException("A senha não pode ser vazia.", nameof(senha));

        if (senha.Length < 8)
            throw new ArgumentException("A senha deve ter no mínimo 8 caracteres.", nameof(senha));

        if (!senha.Any(char.IsUpper))
            throw new ArgumentException("A senha deve conter ao menos uma letra maiúscula.", nameof(senha));

        if (!senha.Any(char.IsDigit))
            throw new ArgumentException("A senha deve conter ao menos um número.", nameof(senha));

        if (senha.All(char.IsLetterOrDigit))
            throw new ArgumentException("A senha deve conter ao menos um caractere especial.", nameof(senha));
    }

    /// <summary>
    /// BCrypt "enhanced" com SHA-384, idêntico ao da Fase 3: os hashes que
    /// vieram do monólito continuam conferindo sem ninguém trocar de senha.
    /// </summary>
    public static string Hash(string senha) =>
        BCrypt.Net.BCrypt.EnhancedHashPassword(senha, BCrypt.Net.HashType.SHA384);

    public static bool Confere(string? senha, string? hash)
    {
        if (string.IsNullOrEmpty(senha) || string.IsNullOrEmpty(hash))
            return false;

        try
        {
            return BCrypt.Net.BCrypt.EnhancedVerify(senha, hash, BCrypt.Net.HashType.SHA384);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            // Hash corrompido no banco é senha que não confere, e não 500.
            return false;
        }
    }

    public static bool EstaBloqueado(Seguranca seguranca, DateTime agora) =>
        seguranca.BloqueadoAte is { } ate && ate > agora;

    /// <summary>Mais uma tentativa errada. Na quinta seguida, bloqueia.</summary>
    public static void RegistrarFalha(Seguranca seguranca, DateTime agora)
    {
        seguranca.TentativasFalhas++;

        if (seguranca.TentativasFalhas >= TentativasAntesDoBloqueio)
        {
            seguranca.BloqueadoAte = agora.Add(DuracaoDoBloqueio);
            seguranca.TentativasFalhas = 0;
        }

        seguranca.AtualizadoEm = agora;
    }

    public static void RegistrarSucesso(Seguranca seguranca, DateTime agora)
    {
        seguranca.TentativasFalhas = 0;
        seguranca.BloqueadoAte = null;
        seguranca.AtualizadoEm = agora;
    }
}
