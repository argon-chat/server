using Argon.Api.Features.CoreLogic.Otp;

namespace Argon.Api.Features.CoreLogic.Otp;

using Temporalio.Activities;
using Temporalio.Workflows;

public enum OtpPurpose { SignIn, ChangeEmail, ResetPassword }
public sealed record SendOtpRequest(string Email, OtpPurpose Purpose, string? DeviceId);
public sealed record VerifyOtpRequest(string Email, OtpPurpose Purpose, string Code, string? DeviceId);

public sealed record OtpRecord(
    string HashBase64,
    string SaltBase64,
    DateTimeOffset Expiry,
    int AttemptsLeft,
    string? RequestId,
    string? DeviceId
);
[Workflow]
public class OtpWorkflow
{
    [WorkflowRun]
    public async Task RunAsync(Guid userId, string email, OtpPurpose purpose, string? deviceId, string ip)
    {
    }
}
public class OtpActivities(IOtpService otp)
{
    //public Task<>

    //[Activity]
    //public Task SendOtpAsync(string email, OtpPurpose purpose, string? deviceId, string ip)
    //{
    //    var req = new SendOtpRequest(email, purpose, deviceId);
    //    return otp.SendAsync(req, ip);
    //}

    //[Activity]
    //public Task<bool> VerifyOtpAsync(string email, OtpPurpose purpose, string code, string? deviceId)
    //{
    //    var req = new VerifyOtpRequest(email, purpose, code, deviceId);
    //    return otp.VerifyAsync(req);
    //}
}