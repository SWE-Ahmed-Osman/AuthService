﻿using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Web;
using Fathy.Common.Auth.Email.Repositories;
using Fathy.Common.Auth.Email.Utilities;
using Fathy.Common.Auth.User.DTOs;
using Fathy.Common.Auth.User.Models;
using Fathy.Common.Auth.User.Utilities;
using Fathy.Common.Startup;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Fathy.Common.Auth.User.Repositories;

public class UserRepository(UserManager<AppUser> userManager, IConfiguration configuration,
        SignInManager<AppUser> signInManager, IEmailRepository emailRepository) : IUserRepository
{
    public async Task<Result> ConfirmEmailAsync(string userEmail, string token)
    {
        var user = await userManager.FindByEmailAsync(userEmail);
        
        return user is null
            ? Result.Failure(new[] { ErrorsList.UserEmailNotFound() })
            : (await userManager.ConfirmEmailAsync(user, token)).ToResult();
    }

    public async Task<Result> CreateAsync(UserDto userDto)
    {
        var createResult = await userManager.CreateAsync(new AppUser
        {
            UserName = userDto.Email,
            Email = userDto.Email,
            FirstName = userDto.FirstName,
            LastName = userDto.LastName
        }, userDto.Password);
        
        return createResult.Succeeded
            ? await SendConfirmationEmailAsync(userDto.Email)
            : createResult.ToResult();
    }

    public async Task<Result> DeleteAsync(SignInDto signInDto)
    {
        var user = await userManager.FindByEmailAsync(signInDto.Email);
        
        return user is null || !await userManager.CheckPasswordAsync(user, signInDto.Password)
            ? Result.Failure(new[] { ErrorsList.WrongEmailOrPassword() })
            : (await userManager.DeleteAsync(user)).ToResult();
    }

    public async Task<Result<AuthDto>> NewRefreshTokenAsync(string refreshToken)
    {
        var user = await userManager.Users.SingleOrDefaultAsync(user =>
            user.RefreshTokens.Any(token => token.Token == refreshToken));

        if (user is null)
            return Result<AuthDto>.Failure(new[] { ErrorsList.InvalidRefreshToken() });

        var token = user.RefreshTokens.Single(token => token.Token == refreshToken);
        
        if (!token.IsActive)
            return Result<AuthDto>.Failure(new[] { ErrorsList.InactiveRefreshToken() });

        token.RevokedOn = DateTime.UtcNow;

        var newRefreshToken = GenerateRefreshToken();
        user.RefreshTokens.Add(newRefreshToken);
        await userManager.UpdateAsync(user);

        return Result<AuthDto>.Success(new AuthDto
        {
            RefreshToken = newRefreshToken.Token,
            JwtSecurityToken = await GenerateJwtSecurityTokenAsync(user),
            RefreshTokenExpiresIn = newRefreshToken.ExpiresIn
        });
    }
    
    public async Task<Result> RevokeRefreshTokenAsync(string refreshToken)
    {
        var user = await userManager.Users.SingleOrDefaultAsync(user =>
            user.RefreshTokens.Any(token => token.Token == refreshToken));
        
        if (user is null)
            return Result.Failure(new[] { ErrorsList.InvalidRefreshToken() });
        
        var token = user.RefreshTokens.Single(token => token.Token == refreshToken);
        
        if (!token.IsActive)
            return Result.Failure(new[] { ErrorsList.InactiveRefreshToken() });
        
        token.RevokedOn = DateTime.UtcNow;
        await userManager.UpdateAsync(user);
        return Result.Success();
    }

    public async Task<Result> SendConfirmationEmailAsync(string userEmail)
    {
        var user = await userManager.FindByEmailAsync(userEmail);
        if (user is null) return Result.Failure(new[] { ErrorsList.UserEmailNotFound() });

        var token = await userManager.GenerateEmailConfirmationTokenAsync(user);

        var confirmEmailUri =
            $"{configuration.GetValue<string>("ConfirmEmailEndpoint")}?userEmail={userEmail}&token={HttpUtility.UrlEncode(token)}";

        var body = "<h1>Welcome</h1><br>" +
                   "<p> Thanks for registering please click " +
                   $"<strong><a href=\"{confirmEmailUri}\" target=\"_blank\">here</a></strong>" +
                   " to confirm your email</p>";

        var message = new Message(userEmail, "Confirmation Email", body)
        {
            IsBodyHtml = true
        };

        return await emailRepository.SendAsync(message);
    }

    public async Task<Result<AuthDto>> SignInAsync(SignInDto signInDto)
    {
        var passwordSignInResult = await signInManager.PasswordSignInAsync(signInDto.Email, signInDto.Password,
            isPersistent: true, lockoutOnFailure: false);
        
        if (passwordSignInResult.IsNotAllowed)
            return Result<AuthDto>.Failure(new[] { ErrorsList.SignInForbidden() });

        if (!passwordSignInResult.Succeeded)
            return Result<AuthDto>.Failure(new[] { ErrorsList.WrongEmailOrPassword() });

        var user = await userManager.FindByEmailAsync(signInDto.Email);
        var authDto = new AuthDto
        {
            JwtSecurityToken = await GenerateJwtSecurityTokenAsync(user!)
        };
        
        if (user!.RefreshTokens.Any(refreshToken =>  refreshToken.IsActive))
        {
            var activeRefreshToken = user.RefreshTokens.FirstOrDefault(refreshToken => refreshToken.IsActive);
            
            authDto.RefreshToken = activeRefreshToken!.Token;
            authDto.RefreshTokenExpiresIn = activeRefreshToken.ExpiresIn;
        }
        else
        {
            var refreshToken = GenerateRefreshToken();
            
            authDto.RefreshToken = refreshToken.Token;
            authDto.RefreshTokenExpiresIn = refreshToken.ExpiresIn;
            
            user.RefreshTokens.Add(refreshToken);
            await userManager.UpdateAsync(user);
        }
        
        return Result<AuthDto>.Success(authDto);
    }

    private async Task<string> GenerateJwtSecurityTokenAsync(AppUser user)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.UserName!),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Iat, DateTime.UtcNow.ToString(CultureInfo.InvariantCulture))
        };

        var roles = await userManager.GetRolesAsync(user);

        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        claims.AddRange(await userManager.GetClaimsAsync(user));

        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: JwtParameters.ValidIssuer,
            audience: JwtParameters.ValidAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(1),
            signingCredentials: new SigningCredentials(JwtParameters.IssuerSigningKey,
                SecurityAlgorithms.HmacSha256)
        ));
    }
    
    private static RefreshToken GenerateRefreshToken() => new()
    {
        Token = Convert.ToBase64String(Guid.NewGuid().ToByteArray()),
        ExpiresIn = DateTime.UtcNow.AddDays(10),
        CreatedOn = DateTime.UtcNow
    };
}