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
    private string? _pendingCode;

    [WorkflowSignal]
    public Task SubmitCodeAsync(string code)
    {
        _pendingCode = code?.Trim();
        return Task.CompletedTask;
    }

    [WorkflowRun]
    public async Task<bool> RunAsync(string email, OtpPurpose purpose, string? deviceId, string ip)
    {
        await Workflow.ExecuteActivityAsync(
            (OtpActivities a) => a.SendOtpAsync(email, purpose, deviceId, ip),
            new() { StartToCloseTimeout = TimeSpan.FromSeconds(10) });

        var expireAt = Workflow.UtcNow.AddMinutes(10);

        while (true)
        {
            var left = expireAt - Workflow.UtcNow;
            if (left <= TimeSpan.Zero)
                return false;

            var got = await Workflow.WaitConditionAsync(() => _pendingCode is not null, left);
            if (!got)
                return false;

            var code = _pendingCode!;
            _pendingCode = null;

            var ok = await Workflow.ExecuteActivityAsync(
                (OtpActivities a) => a.VerifyOtpAsync(email, purpose, code, deviceId),
                new() { StartToCloseTimeout = TimeSpan.FromSeconds(10) });

            if (ok)
                return true;
        }
    }
}

public class OtpActivities(IOtpService otp)
{
    [Activity]
    public Task SendOtpAsync(string email, OtpPurpose purpose, string? deviceId, string ip)
    {
        var req = new SendOtpRequest(email, purpose, deviceId);
        return otp.SendAsync(req, ip);
    }

    [Activity]
    public Task<bool> VerifyOtpAsync(string email, OtpPurpose purpose, string code, string? deviceId)
    {
        var req = new VerifyOtpRequest(email, purpose, code, deviceId);
        return otp.VerifyAsync(req);
    }
}