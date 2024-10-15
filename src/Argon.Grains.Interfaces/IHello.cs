
namespace Argon.Grains.Interfaces;

public interface IHello : IGrainWithGuidCompoundKey
{
    Task<string> DoIt(string who);
}