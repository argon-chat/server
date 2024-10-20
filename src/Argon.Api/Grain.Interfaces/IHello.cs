namespace Argon.Api.Grain.Interfaces;

public interface IHello : IGrainWithIntegerKey
{
    [Alias("Create")]
    Task<string> Create(string who);

    [Alias("GetList")]
    Task<List<string>> GetList();
}