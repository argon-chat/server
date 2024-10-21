namespace Argon.Api.Grains.Interfaces;

public interface IHello : IGrainWithIntegerKey
{
    [Alias("Create")]
    Task<string> Create(string who);

    [Alias("GetList")]
    Task<Dictionary<string, List<string>>> GetList();
}