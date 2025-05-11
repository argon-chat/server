namespace Argon.Users;

public record UsernameReserved
{
    [System.ComponentModel.DataAnnotations.Key]
    public Guid   Id                 { get; set; }
    public string UserName           { get; set; }
    public string NormalizedUserName { get; set; }
    public bool   IsBanned           { get; set; }
    public bool   IsReserved         { get; set; }
}