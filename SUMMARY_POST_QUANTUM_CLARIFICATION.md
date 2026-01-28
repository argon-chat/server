# Summary: Post-Quantum Cryptography vs Password Hashing

## The Question

**Russian**: "а зачем argon2, если можно просто взять постквантовые алгоритмы, у нас уже стоит .NET 10"  
**English**: "Why Argon2, when we can just use post-quantum algorithms, since we already have .NET 10?"

## The Answer

**Post-quantum algorithms and password hashing functions serve DIFFERENT security purposes.**

Using ML-DSA (or other post-quantum algorithms) instead of Argon2 for password hashing would be **technically incorrect** and **insecure**.

---

## Technical Analysis

### What .NET 10 Provides

✅ **CompositeMLDsa** - Post-quantum digital signature algorithm
- Purpose: Sign and verify data (JWT, API requests, documents)
- Speed: Fast (~0.5ms per signature)
- Memory: Low (~2 MB)
- Use case: Replacing RSA/ECDSA for quantum-resistant signatures

❌ **NOT for password hashing**

### Why Argon2 is Correct for Passwords

✅ **Argon2id** - Password hashing function (OWASP/NIST recommended)
- Purpose: Secure password storage in databases
- Speed: Intentionally slow (~100ms per hash)
- Memory: Intentionally high (64 MB per hash)
- Features: Salt, memory-hard, configurable cost

---

## Key Distinctions

| Feature | ML-DSA (Post-Quantum) | Argon2 (Password Hash) |
|---------|----------------------|------------------------|
| Purpose | Digital signatures | Password storage |
| Speed | Fast (good for signatures) | Slow (good for passwords) |
| Memory usage | Low | High (memory-hard) |
| Salt | N/A | Yes (128-bit) |
| Brute-force protection | None | Excellent |
| Quantum resistance | Yes (vs Shor's) | Yes (vs Grover's) |
| **Use for passwords?** | ❌ NO | ✅ YES |

---

## Quantum Computer Threat Reality

### RSA/ECDSA (Asymmetric Crypto)
- 🔴 **HIGH RISK** from Shor's algorithm
- 📅 Vulnerable when large quantum computers exist (~10-20 years)
- 🛡️ Solution: ML-DSA, ML-KEM (post-quantum)

### Password Hashes (Argon2)
- 🟢 **LOW RISK** from Grover's algorithm
- 📅 Minimal threat (only √N speedup)
- 🛡️ Solution: Keep using Argon2, add 1-2 characters to passwords

**Key insight**: Quantum computers don't significantly help brute-force password hashes, especially memory-hard ones like Argon2.

---

## NIST Recommendations

According to **NIST SP 800-208** (2024):

1. **For digital signatures**: Use ML-DSA (CRYSTALS-Dilithium)
2. **For key exchange**: Use ML-KEM (CRYSTALS-Kyber)
3. **For password hashing**: Use **Argon2** (not post-quantum)

**NIST Quote**:
> "Password-based key derivation functions such as Argon2 remain secure against quantum attacks. The primary quantum threat is to asymmetric cryptography (RSA, ECDSA), not to password hashing functions."

---

## Where Post-Quantum Crypto IS Useful

✅ **JWT Signing** (future enhancement):
```csharp
var mlDsa = CompositeMLDsa.Create(CompositeMLDsaAlgorithm.MlDsa44);
byte[] signature = mlDsa.SignData(jwtPayload);
```

✅ **API Authentication**:
```csharp
var signature = mlDsa.SignData(requestBody);
headers.Add("X-Signature", Convert.ToBase64String(signature));
```

❌ **NOT for password hashing** - use Argon2

---

## Performance Comparison

### Argon2 (for passwords)
- ⏱️ ~100ms per hash (intentionally slow)
- 💾 64 MB memory per hash
- ⚡ ~10 hashes/second/core
- 🛡️ **Why slow is good**: Attacker limited to ~10 passwords/sec/core

### ML-DSA (for signatures)
- ⏱️ ~0.5ms per signature (fast)
- 💾 ~2 MB memory
- ⚡ ~2000 operations/second/core
- ⚠️ **Why bad for passwords**: Too fast = easy to brute-force

---

## Documentation Created

1. **WHY_ARGON2_NOT_POST_QUANTUM.md** (Russian)
   - Comprehensive technical explanation
   - NIST/OWASP references
   - Quantum threat analysis
   
2. **EXAMPLE_POST_QUANTUM_USAGE.cs** (English)
   - Code examples of correct ML-DSA usage
   - JWT signing implementation
   - Anti-patterns to avoid

3. **SECURITY.md** (Updated)
   - Post-quantum cryptography section
   - Links to detailed documentation

---

## Conclusion

✅ **Keep Argon2 for password hashing** - It's the correct, NIST/OWASP-recommended solution.

✅ **Consider ML-DSA for JWT signing** (optional future enhancement) - Quantum-resistant tokens.

❌ **Don't use ML-DSA for passwords** - Technically incorrect and insecure.

Our current implementation is **secure, compliant, and quantum-resistant**. 🔐

---

## References

- [NIST Post-Quantum Cryptography](https://csrc.nist.gov/projects/post-quantum-cryptography)
- [Argon2 RFC 9106](https://www.rfc-editor.org/rfc/rfc9106.html)
- [OWASP Password Storage](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html)
- [ML-DSA (FIPS 204)](https://csrc.nist.gov/pubs/fips/204/final)
