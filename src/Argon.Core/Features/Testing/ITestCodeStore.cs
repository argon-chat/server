namespace Argon.Features.Testing;

/// <summary>
/// Interface for storing and retrieving verification codes during testing.
/// Used by NullPhoneChannel and EmailManager when SMTP is disabled.
/// </summary>
public interface ITestCodeStore
{
    /// <summary>
    /// Store a verification code for a target (email or phone).
    /// </summary>
    void StoreCode(string target, string code, TestCodeType type);

    /// <summary>
    /// Get the latest verification code for a target.
    /// </summary>
    string? GetCode(string target, TestCodeType type);

    /// <summary>
    /// Get the latest verification code for a target, waiting if not yet available.
    /// </summary>
    Task<string?> GetCodeAsync(string target, TestCodeType type, TimeSpan timeout, CancellationToken ct = default);

    /// <summary>
    /// Clear all stored codes.
    /// </summary>
    void Clear();
}

public enum TestCodeType
{
    Email,
    Phone
}
