// Example: How to properly use post-quantum cryptography in Argon Server
// This demonstrates ML-DSA for JWT signing, NOT for password hashing

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Argon.Examples.PostQuantum;

/// <summary>
/// Example of CORRECT post-quantum cryptography usage.
/// ML-DSA is used for JWT signing, while Argon2 remains for password hashing.
/// </summary>
public class PostQuantumJWTExample
{
    // ✅ CORRECT: Use Argon2 for passwords (as we currently do)
    // See: src/Argon.Core/Services/IPasswordHashingService.cs
    
    // ✅ CORRECT: Use ML-DSA for JWT signatures (example below)
    
    /// <summary>
    /// Example: Create a JWT with post-quantum signature
    /// This would replace ES256/RS256 signatures to be quantum-resistant
    /// </summary>
    public static string CreatePostQuantumJWT(Dictionary<string, object> claims)
    {
        // Check if ML-DSA is supported on this platform
        if (!CompositeMLDsa.IsAlgorithmSupported(CompositeMLDsaAlgorithm.MlDsa44))
        {
            throw new PlatformNotSupportedException(
                "ML-DSA-44 not supported on this platform. " +
                "Requires Windows 11 24H2+ or compatible Linux with post-quantum support.");
        }
        
        // Create JWT header with post-quantum algorithm
        var header = new
        {
            alg = "ML-DSA-44",  // Post-quantum signature algorithm
            typ = "JWT"
        };
        
        // Serialize claims
        var headerJson = JsonSerializer.Serialize(header);
        var payloadJson = JsonSerializer.Serialize(claims);
        
        // Base64URL encode
        var headerB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var payloadB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        
        // Create signing input
        var signingInput = $"{headerB64}.{payloadB64}";
        var messageBytes = Encoding.UTF8.GetBytes(signingInput);
        
        // Generate post-quantum signature
        using var mlDsa = CompositeMLDsa.Create(CompositeMLDsaAlgorithm.MlDsa44);
        var signatureBytes = mlDsa.SignData(messageBytes);
        var signatureB64 = Base64UrlEncode(signatureBytes);
        
        // Return complete JWT
        return $"{signingInput}.{signatureB64}";
    }
    
    /// <summary>
    /// Verify a post-quantum JWT
    /// </summary>
    public static bool VerifyPostQuantumJWT(string jwt, CompositeMLDsa publicKey)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3)
            return false;
            
        var signingInput = $"{parts[0]}.{parts[1]}";
        var messageBytes = Encoding.UTF8.GetBytes(signingInput);
        var signatureBytes = Base64UrlDecode(parts[2]);
        
        return publicKey.VerifyData(messageBytes, signatureBytes);
    }
    
    private static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
    
    private static byte[] Base64UrlDecode(string input)
    {
        var base64 = input
            .Replace('-', '+')
            .Replace('_', '/');
            
        switch (base64.Length % 4)
        {
            case 2: base64 += "=="; break;
            case 3: base64 += "="; break;
        }
        
        return Convert.FromBase64String(base64);
    }
}

/// <summary>
/// Example of where post-quantum crypto would be integrated
/// This is for DEMONSTRATION only - not production code
/// </summary>
public class PostQuantumIntegrationExample
{
    // ❌ WRONG: Don't use ML-DSA for passwords!
    // public string HashPassword(string password)
    // {
    //     var mlDsa = CompositeMLDsa.Create(...);
    //     return mlDsa.SignData(...); // NO! This is wrong!
    // }
    
    // ✅ CORRECT: Keep using Argon2 for passwords (current implementation)
    // See: src/Argon.Core/Services/IPasswordHashingService.cs
    
    // ✅ CORRECT: Use ML-DSA for JWT signing
    public string IssuePostQuantumAccessToken(Guid userId)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = userId.ToString(),
            ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["exp"] = DateTimeOffset.UtcNow.AddHours(7).ToUnixTimeSeconds(),
            ["iss"] = "Argon"
        };
        
        return PostQuantumJWTExample.CreatePostQuantumJWT(claims);
    }
    
    // ✅ CORRECT: Use ML-DSA for API request signing
    public string SignAPIRequest(byte[] requestBody)
    {
        using var mlDsa = CompositeMLDsa.Create(CompositeMLDsaAlgorithm.MlDsa44);
        var signature = mlDsa.SignData(requestBody);
        return Convert.ToBase64String(signature);
    }
}

/// <summary>
/// Why Argon2 vs ML-DSA for passwords?
/// </summary>
public static class SecurityExplanation
{
    public const string ARGON2_PURPOSE = @"
    Argon2id Purpose:
    ✅ Password hashing (our current use)
    ✅ Slow by design (prevents brute force)
    ✅ Memory-hard (prevents GPU/ASIC attacks)
    ✅ Uses salt (prevents rainbow tables)
    ✅ Configurable cost (future-proof)
    
    Why quantum-resistant?
    - Hash functions are inherently quantum-resistant
    - Grover's algorithm only gives √N speedup
    - Memory-hard design mitigates quantum advantage
    - Adding 2 characters to password compensates for quantum speedup
    ";
    
    public const string ML_DSA_PURPOSE = @"
    ML-DSA (Post-Quantum) Purpose:
    ✅ Digital signatures (JWT, API auth)
    ✅ Quantum-resistant signatures
    ✅ Fast verification (good for high-throughput)
    ❌ NOT for password hashing
    ❌ No salt mechanism
    ❌ Fast by design (bad for passwords)
    ❌ Not memory-hard
    
    Why needed?
    - RSA/ECDSA vulnerable to quantum computers (Shor's algorithm)
    - JWT signatures need quantum resistance
    - ML-DSA provides post-quantum security for signatures
    ";
    
    public const string QUANTUM_THREAT_REALITY = @"
    Quantum Computer Threat Timeline:
    
    For RSA/ECDSA (asymmetric crypto):
    ⚠️  HIGH RISK - Vulnerable to Shor's algorithm
    📅 Threat: 10-20 years (when large quantum computers exist)
    🛡️  Solution: ML-DSA, ML-KEM (post-quantum)
    
    For Password Hashes (Argon2):
    ✅ LOW RISK - Only vulnerable to Grover's algorithm
    📅 Threat: Minimal (√N speedup easily compensated)
    🛡️  Solution: Keep using Argon2, increase password length by 1-2 chars
    
    Reality Check:
    - No practical quantum computer exists today for breaking crypto
    - RSA/ECDSA are the main targets
    - Hash functions (including password hashing) remain secure
    - Memory-hard functions are particularly resistant
    ";
}

/// <summary>
/// Performance comparison
/// </summary>
public static class PerformanceComparison
{
    public const string ARGON2_PERFORMANCE = @"
    Argon2id (for passwords):
    ⏱️  Hash time: ~100ms (intentionally slow)
    💾 Memory: 64 MB per hash
    🔢 Iterations: 3
    ⚡ Throughput: ~10 hashes/second/core
    
    Why slow is good:
    - Attacker can only try 10 passwords/second/core
    - With 1000 cores: 10,000 passwords/second
    - 8-char random password: 2^47 combinations
    - Brute force time: ~400 years
    ";
    
    public const string ML_DSA_PERFORMANCE = @"
    ML-DSA-44 (for signatures):
    ⏱️  Sign time: ~0.5ms (fast)
    ⏱️  Verify time: ~0.3ms (fast)
    💾 Memory: ~2 MB
    ⚡ Throughput: ~2000 operations/second/core
    
    Why fast is good:
    - Can verify thousands of JWTs per second
    - Low latency for API authentication
    - Suitable for high-throughput services
    
    Why bad for passwords:
    - Too fast = easy to brute force
    - No memory hardness = GPU attacks effective
    - Not designed for password hashing
    ";
}
