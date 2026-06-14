using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Ccusage.Api.Tests.Integration;

/// <summary>Mints app JWTs the way GoogleAuthService does, for driving authorized requests in tests.</summary>
public static class TestJwt
{
    public static string For(string email, string? key = null)
    {
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key ?? WebAppFactory.Key)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: "usage-iq",
            audience: "usage-iq",
            claims: new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, email),
                new Claim("email", email),
                new Claim("name", email),
            },
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
