using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using PosTech.Identidade;

namespace PosTech.Identidade.Api;

public sealed record TokenEmitido(string AccessToken, int ExpiresIn, int FuncionarioId, string Cargo);

/// <summary>
/// Emite o JWT do funcionário — o mesmo formato da Fase 3.
///
/// Mesmas chaves de configuração (<c>Jwt:SecretKey</c>, <c>Jwt:Issuer</c>,
/// <c>Jwt:Audience</c>), mesmo algoritmo e as mesmas claims: o cargo como
/// <c>role</c> e o id em <c>FuncionarioId</c>. É isso que a política
/// <c>validate-jwt</c> do APIM e o segredo do Key Vault já esperam; mudar o
/// formato aqui obrigaria a mudar o gateway junto.
/// </summary>
public sealed class EmissorDeToken
{
    /// <summary>HS256 exige chave de pelo menos 256 bits.</summary>
    public const int TamanhoMinimoDaChave = 32;

    private readonly SymmetricSecurityKey _chave;
    private readonly string _emissor;
    private readonly string _audiencia;
    private readonly int _minutos;

    public EmissorDeToken(IConfiguration configuracao)
    {
        var segredo = configuracao["Jwt:SecretKey"]
            ?? throw new InvalidOperationException("Jwt:SecretKey não configurado.");

        if (Encoding.UTF8.GetByteCount(segredo) < TamanhoMinimoDaChave)
            throw new InvalidOperationException($"Jwt:SecretKey precisa de ao menos {TamanhoMinimoDaChave} bytes.");

        _chave = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(segredo));
        _emissor = configuracao["Jwt:Issuer"] ?? throw new InvalidOperationException("Jwt:Issuer não configurado.");
        _audiencia = configuracao["Jwt:Audience"] ?? throw new InvalidOperationException("Jwt:Audience não configurado.");
        _minutos = int.TryParse(configuracao["Jwt:AccessTokenExpirationMinutes"], out var m) && m > 0 ? m : 15;
    }

    public TokenEmitido Emitir(Funcionario funcionario)
    {
        var cargo = funcionario.Cargo.ToString();

        var descritor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, funcionario.Id.ToString()),
                new Claim(ClaimTypes.Role, cargo),
                new Claim("FuncionarioId", funcionario.Id.ToString()),
            ]),
            Expires = DateTime.UtcNow.AddMinutes(_minutos),
            Issuer = _emissor,
            Audience = _audiencia,
            SigningCredentials = new SigningCredentials(_chave, SecurityAlgorithms.HmacSha256),
        };

        var manipulador = new JwtSecurityTokenHandler();
        var token = manipulador.WriteToken(manipulador.CreateToken(descritor));

        return new TokenEmitido(token, _minutos * 60, funcionario.Id, cargo);
    }
}
