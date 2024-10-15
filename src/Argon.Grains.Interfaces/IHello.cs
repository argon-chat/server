
namespace Argon.Grains.Interfaces;

public interface IHello : IGrainWithGuidCompoundKey
{
    Task DoIt(string who);
}