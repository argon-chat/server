namespace Argon.Features.PhoneProviders;

public interface IPhoneProvider
{
    Task<string> SendCode(string phone);
    Task<bool>   VerifyCode(string phone, string requestId, string otpCode);
}

public class NullPhoneProvider : IPhoneProvider
{
    public Task<string> SendCode(string phone)
        => throw new NotImplementedException();

    public Task<bool> VerifyCode(string phone, string requestId, string otpCode)
        => throw new NotImplementedException();
}

public static class PhoneProviderEx
{
    public static void AddPhoneOtpProvider(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IPhoneProvider, NullPhoneProvider>();
    }
}