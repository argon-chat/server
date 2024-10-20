namespace Argon.Api.Entities;

public class ApplicationUser : ApplicationRecord
{
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}