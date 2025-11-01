namespace Argon.Core.Features.Integrations.Phones;

using Argon.Features.Integrations.Phones.Prelude;
using Argon.Features.Integrations.Phones.Telegram;

public record OtpStartResult(PhoneChannelKind kind);
public enum VerifyStatus { Verified, WrongCode, TooManyAttempts }

public record VerifyResult(VerifyStatus verifyResult, int attemptCount,
    DateTime? RetryAfterUtc = null);

//[Workflow]
//public class PhoneOtpWorkflow
//{
//    private string _phone = null!;
//    private bool _viaTelegram;
//    private GatewayRequestId? _tgReqId;
//    private PreludeRequestId? _preludeReqId;

//    private bool _verified;
//    private int _attempts;

//    private int _maxAttempts;
//    private TimeSpan _cooldown;
//    private DateTime _nextAllowedAttemptUtc;
//    private DateTime _expiresAtUtc;

//    private static readonly ActivityOptions SendOpts = new()
//    {
//        StartToCloseTimeout = TimeSpan.FromSeconds(30)
//    };
//    private static readonly ActivityOptions CheckOpts = new()
//    {
//        StartToCloseTimeout = TimeSpan.FromSeconds(15)
//    };

//    [WorkflowRun]
//    public async Task<OtpStartResult> RunAsync(
//        string phoneNumber,
//        string userIp,
//        string userAgent,
//        string appVersion,
//        int codeLen = 6,
//        int maxAttempts = 5,
//        TimeSpan? codeTtl = null,
//        TimeSpan? attemptCooldown = null)
//    {
//        _phone = phoneNumber;
//        _maxAttempts = maxAttempts;
//        _cooldown = attemptCooldown ?? TimeSpan.Zero;
//        _nextAllowedAttemptUtc = Workflow.UtcNow; 
//        _expiresAtUtc = Workflow.UtcNow + (codeTtl ?? TimeSpan.FromMinutes(5));

//        var canTg = await Workflow.ExecuteActivityAsync(
//            (PhoneActivities a) => a.CheckTelegramAbilityAsync(phoneNumber), SendOpts);

//        if (canTg)
//        {
//            var req = await Workflow.ExecuteActivityAsync(
//                (PhoneActivities a) => a.SendTelegramAsync(phoneNumber, codeLen), SendOpts);
//            if (req is null)
//                throw new ApplicationFailureException("Telegram send failed", nonRetryable: true);

//            _viaTelegram = true;
//            _tgReqId = req;
//        }
//        else
//        {
//            var pre = await Workflow.ExecuteActivityAsync(
//                (PhoneActivities a) => a.SendPreludeAsync(phoneNumber, userIp, userAgent, appVersion), SendOpts);
//            if (pre is null)
//                throw new ApplicationFailureException("Prelude send failed", nonRetryable: true);

//            _viaTelegram = false;
//            _preludeReqId = pre;
//        }

//        await Workflow.WaitConditionAsync(
//            () => _verified || Workflow.UtcNow >= _expiresAtUtc);

//        await Workflow.WaitConditionAsync(() => Workflow.AllHandlersFinished);

//        return new OtpStartResult(
//            _viaTelegram ? PhoneChannelKind.Telegram : PhoneChannelKind.Prelude);
//    }

//    [WorkflowUpdateValidator(nameof(VerifyAsync))]
//    public void ValidateVerify(string code)
//    {
//        if (string.IsNullOrWhiteSpace(code))
//            throw new ApplicationFailureException("Empty code");

//        if (Workflow.UtcNow >= _expiresAtUtc)
//            throw new ApplicationFailureException("Code expired");

//        if (_verified)
//            throw new ApplicationFailureException("Already verified");

//        if (_attempts >= _maxAttempts)
//            throw new ApplicationFailureException("Too many attempts");

//        if (Workflow.UtcNow < _nextAllowedAttemptUtc)
//            throw new ApplicationFailureException($"Too soon; retry after {_nextAllowedAttemptUtc:O}");
//    }

//    [WorkflowUpdate]
//    public async Task<VerifyResult> VerifyAsync(string code)
//    {
//        if (_cooldown > TimeSpan.Zero && Workflow.UtcNow < _nextAllowedAttemptUtc)
//        {
//            await Workflow.DelayAsync(_nextAllowedAttemptUtc - Workflow.UtcNow); 
//        }

//        var ok = _viaTelegram && _tgReqId is not null
//            ? await Workflow.ExecuteActivityAsync(
//                (PhoneActivities a) => a.CheckTelegramCodeAsync(_tgReqId.Value, code), CheckOpts)
//            : await Workflow.ExecuteActivityAsync(
//                (PhoneActivities a) => a.CheckPreludeCodeAsync(_phone, code), CheckOpts);

//        if (ok)
//        {
//            _verified = true;
//            return new VerifyResult(VerifyStatus.Verified, _maxAttempts - _attempts);
//        }

//        _attempts++;
//        if (_cooldown > TimeSpan.Zero)
//            _nextAllowedAttemptUtc = Workflow.UtcNow + _cooldown;

//        if (_attempts >= _maxAttempts)
//            return new VerifyResult(VerifyStatus.TooManyAttempts, 0);

//        return new VerifyResult(
//            VerifyStatus.WrongCode,
//            _maxAttempts - _attempts,
//            _cooldown > TimeSpan.Zero ? _nextAllowedAttemptUtc : null);
//    }

//    [WorkflowQuery]
//    public string Status =>
//        _verified ? "verified"
//        : (Workflow.UtcNow >= _expiresAtUtc ? "expired" : "pending");
//}

//public class PhoneActivities(TelegramGateway tg, PreludeGateway prelude)
//{
//    [Activity]
//    public Task<bool> CheckTelegramAbilityAsync(string phoneNumber)
//        => tg.CheckSendAbilityAsync(phoneNumber, ActivityExecutionContext.Current.CancellationToken);

//    [Activity]
//    public Task<GatewayRequestId?> SendTelegramAsync(string phoneNumber, int codeLen = 6)
//        => tg.SendVerificationMessage(phoneNumber, codeLen, ActivityExecutionContext.Current.CancellationToken);

//    [Activity]
//    public Task<bool> CheckTelegramCodeAsync(GatewayRequestId requestId, string code)
//        => tg.CheckVerificationStatus(requestId, code, ActivityExecutionContext.Current.CancellationToken);

//    [Activity]
//    public Task<PreludeRequestId?> SendPreludeAsync(
//        string phoneNumber, string ip, string userAgent, string appVersion)
//        => prelude.SendVerificationAsync(
//            phoneNumber, ip, userAgent, appVersion, ActivityExecutionContext.Current.CancellationToken);

//    [Activity]
//    public Task<bool> CheckPreludeCodeAsync(string phoneNumber, string code)
//        => prelude.CheckVerificationAsync(phoneNumber, code, ActivityExecutionContext.Current.CancellationToken);
//}