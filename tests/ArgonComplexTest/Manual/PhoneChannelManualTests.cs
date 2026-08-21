namespace ArgonComplexTest.Manual;

using Argon.Features.Integrations.Phones;
using Argon.Features.Integrations.Phones.Telegram;
using Argon.Features.Integrations.Phones.Prelude;
using Argon.Features.Integrations.Phones.Twilio;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Manual tests for phone verification channels.
/// Fill in your tokens and run individually.
/// These tests are marked as [Explicit] so they don't run in CI.
/// </summary>
[TestFixture]
public class PhoneChannelManualTests
{
    // ========================================
    // FILL IN YOUR TEST DATA HERE
    // ========================================
    
    private const string TestPhoneNumber = "+79998887766"; // Your test phone number
    
    // Telegram Gateway (https://core.telegram.org/gateway)
    private const string TelegramToken = "TG_TOKEN";
    private const string TelegramEndpoint = "https://gatewayapi.telegram.org";
    
    // Prelude (https://prelude.dev)
    private const string PreludeToken = "PRELUDE_TOKEN";
    private const string PreludeEndpoint = "https://api.prelude.dev";
    
    // Twilio (https://twilio.com)
    private const string TwilioAccountSid = "YOUR_TWILIO_ACCOUNT_SID";
    private const string TwilioAuthToken = "YOUR_TWILIO_AUTH_TOKEN";
    private const string TwilioVerifyServiceSid = "YOUR_TWILIO_VERIFY_SERVICE_SID";

    // ========================================

    private ILogger<T> CreateLogger<T>() => LoggerFactory
        .Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug))
        .CreateLogger<T>();

    #region Telegram Tests

    [Test, Explicit("Manual test - requires valid Telegram token")]
    public async Task Telegram_CheckSendAbility()
    {
        var options = CreateOptions(new PhoneVerificationOptions
        {
            Enabled = true,
            Telegram = new TelegramChannelOptions
            {
                Enabled = true,
                Token = TelegramToken,
                Endpoint = TelegramEndpoint
            }
        });

        var channel = new TelegramPhoneChannel(CreateLogger<TelegramPhoneChannel>(), options);

        var canSend = await channel.CanSendAsync(TestPhoneNumber);

        Console.WriteLine($"Can send to {TestPhoneNumber}: {canSend}");
        Assert.Pass($"CanSend result: {canSend}");
    }

    [Test, Explicit("Manual test - requires valid Telegram token")]
    public async Task Telegram_SendCode()
    {
        var options = CreateOptions(new PhoneVerificationOptions
        {
            Enabled = true,
            Telegram = new TelegramChannelOptions
            {
                Enabled = true,
                Token = TelegramToken,
                Endpoint = TelegramEndpoint
            }
        });

        var channel = new TelegramPhoneChannel(CreateLogger<TelegramPhoneChannel>(), options);

        var request = new PhoneSendRequest(TestPhoneNumber, "127.0.0.1", "ManualTest", "1.0");
        var result = await channel.SendCodeAsync(request);

        Console.WriteLine($"Success: {result.Success}");
        Console.WriteLine($"RequestId: {result.RequestId}");
        Console.WriteLine($"Error: {result.ErrorReason}");

        if (result.Success)
        {
            Assert.Pass($"Code sent! RequestId: {result.RequestId}");
        }
        else
        {
            Assert.Fail($"Failed to send: {result.ErrorReason}");
        }
    }

    [Test, Explicit("Manual test - requires valid Telegram token and request ID")]
    public async Task Telegram_VerifyCode()
    {
        // Fill these in after running Telegram_SendCode
        const string requestId = "123";
        const string code = "333444"; // Code you received

        var options = CreateOptions(new PhoneVerificationOptions
        {
            Enabled = true,
            Telegram = new TelegramChannelOptions
            {
                Enabled = true,
                Token = TelegramToken,
                Endpoint = TelegramEndpoint
            }
        });

        var channel = new TelegramPhoneChannel(CreateLogger<TelegramPhoneChannel>(), options);

        var request = new PhoneVerifyRequest(TestPhoneNumber, requestId, code);
        var result = await channel.VerifyCodeAsync(request);

        Console.WriteLine($"Status: {result.Status}");
        Console.WriteLine($"Remaining attempts: {result.RemainingAttempts}");

        Assert.Pass($"Verification result: {result.Status}");
    }

    #endregion

    #region Prelude Tests

    [Test, Explicit("Manual test - requires valid Prelude token")]
    public async Task Prelude_SendCode()
    {
        var options = CreateOptions(new PhoneVerificationOptions
        {
            Enabled = true,
            Prelude = new PreludeChannelOptions
            {
                Enabled = true,
                Token = PreludeToken,
                Endpoint = PreludeEndpoint
            }
        });

        var channel = new PreludePhoneChannel(CreateLogger<PreludePhoneChannel>(), options);

        var request = new PhoneSendRequest(TestPhoneNumber, "127.0.0.1", "ManualTest/1.0", "1.0");
        var result = await channel.SendCodeAsync(request);

        Console.WriteLine($"Success: {result.Success}");
        Console.WriteLine($"RequestId: {result.RequestId}");
        Console.WriteLine($"Error: {result.ErrorReason}");

        if (result.Success)
        {
            Assert.Pass($"Code sent! RequestId: {result.RequestId}");
        }
        else
        {
            Assert.Fail($"Failed to send: {result.ErrorReason}");
        }
    }

    [Test, Explicit("Manual test - requires valid Prelude token")]
    public async Task Prelude_VerifyCode()
    {
        const string code = "444333"; // Code you received via SMS

        var options = CreateOptions(new PhoneVerificationOptions
        {
            Enabled = true,
            Prelude = new PreludeChannelOptions
            {
                Enabled = true,
                Token = PreludeToken,
                Endpoint = PreludeEndpoint
            }
        });

        var channel = new PreludePhoneChannel(CreateLogger<PreludePhoneChannel>(), options);

        // Prelude uses phone number for verification, not request ID
        var request = new PhoneVerifyRequest(TestPhoneNumber, null, code);
        var result = await channel.VerifyCodeAsync(request);

        Console.WriteLine($"Status: {result.Status}");

        Assert.Pass($"Verification result: {result.Status}");
    }

    #endregion

    #region Twilio Tests

    [Test, Explicit("Manual test - requires valid Twilio credentials")]
    public async Task Twilio_SendCode()
    {
        var options = CreateOptions(new PhoneVerificationOptions
        {
            Enabled = true,
            Twilio = new TwilioChannelOptions
            {
                Enabled = true,
                AccountSid = TwilioAccountSid,
                AuthToken = TwilioAuthToken,
                VerifyServiceSid = TwilioVerifyServiceSid
            }
        });

        var channel = new TwilioPhoneChannel(CreateLogger<TwilioPhoneChannel>(), options);

        var request = new PhoneSendRequest(TestPhoneNumber, "127.0.0.1", "ManualTest", "1.0");
        var result = await channel.SendCodeAsync(request);

        Console.WriteLine($"Success: {result.Success}");
        Console.WriteLine($"RequestId (SID): {result.RequestId}");
        Console.WriteLine($"Error: {result.ErrorReason}");

        if (result.Success)
        {
            Assert.Pass($"Code sent! SID: {result.RequestId}");
        }
        else
        {
            Assert.Fail($"Failed to send: {result.ErrorReason}");
        }
    }

    [Test, Explicit("Manual test - requires valid Twilio credentials")]
    public async Task Twilio_VerifyCode()
    {
        const string code = "123456"; // Code you received via SMS

        var options = CreateOptions(new PhoneVerificationOptions
        {
            Enabled = true,
            Twilio = new TwilioChannelOptions
            {
                Enabled = true,
                AccountSid = TwilioAccountSid,
                AuthToken = TwilioAuthToken,
                VerifyServiceSid = TwilioVerifyServiceSid
            }
        });

        var channel = new TwilioPhoneChannel(CreateLogger<TwilioPhoneChannel>(), options);

        // Twilio uses phone number for verification
        var request = new PhoneVerifyRequest(TestPhoneNumber, null, code);
        var result = await channel.VerifyCodeAsync(request);

        Console.WriteLine($"Status: {result.Status}");

        Assert.Pass($"Verification result: {result.Status}");
    }

    #endregion

    #region Full Flow Tests

    [Test, Explicit("Manual test - full verification flow via PhoneVerificationService")]
    public async Task FullFlow_SendAndVerify()
    {
        var options = CreateOptions(new PhoneVerificationOptions
        {
            Enabled = true,
            Telegram = new TelegramChannelOptions
            {
                Enabled = true,
                Token = TelegramToken,
                Endpoint = TelegramEndpoint
            },
            Prelude = new PreludeChannelOptions
            {
                Enabled = true,
                Token = PreludeToken,
                Endpoint = PreludeEndpoint
            },
            Twilio = new TwilioChannelOptions
            {
                Enabled = false // Enable if you have Twilio
            }
        });

        var nullChannel = new NullPhoneChannel(CreateLogger<NullPhoneChannel>());
        
        // Create service provider mock
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton(CreateLogger<TelegramPhoneChannel>());
        services.AddSingleton(CreateLogger<PreludePhoneChannel>());
        services.AddSingleton(CreateLogger<TwilioPhoneChannel>());
        services.AddSingleton<TelegramPhoneChannel>();
        services.AddSingleton<PreludePhoneChannel>();
        services.AddSingleton<TwilioPhoneChannel>();
        var sp = services.BuildServiceProvider();

        var service = new PhoneVerificationService(
            CreateLogger<PhoneVerificationService>(),
            options,
            nullChannel,
            sp);

        // Step 1: Send code
        Console.WriteLine("=== Sending code ===");
        await service.SendCode(TestPhoneNumber, "127.0.0.1", "ManualTest", "1.0");
        Console.WriteLine("Code sent (check logs for channel used)");

        // Step 2: Wait for user to enter code
        Console.WriteLine("\n=== Enter the code you received ===");
        Console.Write("Code: ");
        var code = Console.ReadLine() ?? "";

        // Step 3: Verify
        Console.WriteLine("\n=== Verifying code ===");
        var result = await service.VerifyCode(TestPhoneNumber, "", code);

        Console.WriteLine($"Result: {result.verifyResult}");
        Console.WriteLine($"Attempts left: {result.attemptCount}");

        if (result.verifyResult == VerifyStatus.Verified)
        {
            Assert.Pass("Verification successful!");
        }
        else
        {
            Assert.Fail($"Verification failed: {result.verifyResult}");
        }
    }

    #endregion

    #region Helpers

    private static IOptions<PhoneVerificationOptions> CreateOptions(PhoneVerificationOptions options)
        => Options.Create(options);

    #endregion
}
