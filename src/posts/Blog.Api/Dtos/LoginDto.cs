namespace Blog.Api.Dtos;

using System.ComponentModel.DataAnnotations;

public class LoginDto
{
    [Required] public string Username { get; set; } = string.Empty;
    [Required] public string Password { get; set; } = string.Empty;
}