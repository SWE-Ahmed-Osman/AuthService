using System.ComponentModel.DataAnnotations;

namespace AuthService.DTOs;

public class SignInDto
{
    [Required, EmailAddress] public string Email { get; init; } = null!;
    [Required] public string Password { get; init; } = null!;
}