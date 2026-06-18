using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Ccusage.Api.Tests.Integration;

/// <summary>Mints app JWTs the way GoogleAuthService does, for driving authorized requests in tests.</summary>
public static class TestJwt
{
    /// <param name="sv">
    /// The session-version stamp. When null the <c>sv</c> claim is omitted entirely (a pre-stamp token);
    /// the pipeline treats a missing <c>sv</c> as 0. Pass an int to mint a token with an explicit stamp.
    /// </param>
    public static string For(string email, string? key = null, int? sv = null)
    {
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key ?? WebAppFactory.Key)),
            SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, email),
            new("email", email),
            new("name", email),
        };
        if (sv is not null) claims.Add(new Claim("sv", sv.Value.ToString()));

        var token = new JwtSecurityToken(
            issuer: "usage-iq",
            audience: "usage-iq",
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
