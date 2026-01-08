namespace Argon.Features.Integrations.Phones;

using Prelude;
using Telegram;
using Twilio;

public interface IPhoneProvider
{
    Task              SendCode(string phone, string ip, string ua, string appVersion);
    Task<VerifyResult> VerifyCode(string phone, string requestId, string otpCode);
}

public static class PhoneProviderExtensions
{
    public static void AddPhoneVerification(this WebApplicationBuilder builder)
    {
        // Register configuration
        builder.Services.Configure<PhoneVerificationOptions>(
            builder.Configuration.GetSection("Phone"));

        // Register null channel (always available)
        builder.Services.AddSingleton<NullPhoneChannel>();

        // Register real channels
        builder.Services.AddSingleton<TelegramPhoneChannel>();
        builder.Services.AddSingleton<PreludePhoneChannel>();
        builder.Services.AddSingleton<TwilioPhoneChannel>();

        // Register the main service
        builder.Services.AddSingleton<IPhoneProvider, PhoneVerificationService>();
    }
}