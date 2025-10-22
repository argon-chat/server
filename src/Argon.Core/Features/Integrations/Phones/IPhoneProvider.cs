namespace Argon.Core.Features.Integrations.Phones;

using Argon.Features.Integrations.Phones.Prelude;
using Argon.Features.Integrations.Phones.Telegram;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;

public interface IPhoneProvider
{
    Task               SendCode(string phone, string ip, string ua, string appVersion);
    Task<VerifyResult> VerifyCode(string phone, string requestId, string otpCode);
}

public class NullPhoneProvider : IPhoneProvider
{
    public Task SendCode(string phone, string ip, string ua, string appVersion)
        => throw new NotImplementedException();

    public Task<VerifyResult> VerifyCode(string phone, string requestId, string otpCode)
        => throw new NotImplementedException();
}

public class DefaultPhoneProvider(ITemporalClient client) : IPhoneProvider
{
    public async Task SendCode(string phone, string ip, string ua, string appVersion)
        => await client.StartWorkflowAsync(
            (PhoneOtpWorkflow wf) => wf.RunAsync(phone, ip, ua, appVersion, 6, 3,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromSeconds(5)),
            new($"otp:{phone}", "otp-queue")
            {
                IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting
            });

    public async Task<VerifyResult> VerifyCode(string phone, string requestId, string otpCode)
    {
        var handle = client.GetWorkflowHandle<PhoneOtpWorkflow>(
            $"otp:{phone}"
        );

        var result = await handle.ExecuteUpdateAsync(
            wf => wf.VerifyAsync(otpCode)
        );

        return result;
    }
}

public static class PhoneProviderEx
{
    public static void AddPhoneOtpProvider(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IPhoneProvider, NullPhoneProvider>();
        builder.AddPreludeGateway();
        builder.AddTelegramGateway();
    }
}