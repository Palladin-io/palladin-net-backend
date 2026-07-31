using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Konscious.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Features;

namespace Palladin.Tests.Unit.Modules.Identity;

public sealed class IdentityKdfVectorTests
{
    [Fact]
    public void PasswordOnlyV1Fixture_MatchesFrozenDerivation()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "IdentityKdf", "password-only-v1.json")));
        var root = fixture.RootElement;
        var argon = root.GetProperty("argon2");
        var password = Encoding.UTF8.GetBytes(root.GetProperty("passwordUtf8").GetString()!);
        var salt = WebEncoders.Base64UrlDecode(root.GetProperty("kdfSaltBase64Url").GetString()!);
        using var argon2 = new Argon2id(password)
        {
            Salt = salt,
            MemorySize = argon.GetProperty("memoryKiB").GetInt32(),
            Iterations = argon.GetProperty("iterations").GetInt32(),
            DegreeOfParallelism = argon.GetProperty("parallelism").GetInt32(),
        };
        var accountRoot = argon2.GetBytes(argon.GetProperty("outputBytes").GetInt32());
        var accountId = root.GetProperty("accountId").GetGuid();
        var accountIdBytes = new byte[16];
        accountId.TryWriteBytes(accountIdBytes, bigEndian: true, out _);
        var hkdf = root.GetProperty("hkdf");
        var auth = HKDF.DeriveKey(HashAlgorithmName.SHA256, accountRoot, hkdf.GetProperty("outputBytes").GetInt32(),
            accountIdBytes, Encoding.UTF8.GetBytes(hkdf.GetProperty("authCredentialInfoUtf8").GetString()!));
        var masterKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, accountRoot, hkdf.GetProperty("outputBytes").GetInt32(),
            accountIdBytes, Encoding.UTF8.GetBytes(hkdf.GetProperty("masterKeyInfoUtf8").GetString()!));
        var expected = root.GetProperty("expected");

        root.GetProperty("profileId").GetString().ShouldBe(IdentityKdfProfiles.CurrentProfileId);
        root.GetProperty("securityVersion").GetUInt16().ShouldBe(IdentityKdfProfiles.CurrentSecurityVersion);
        hkdf.GetProperty("authCredentialInfoUtf8").GetString().ShouldBe(IdentityKdfProfiles.AuthCredentialInfo);
        hkdf.GetProperty("masterKeyInfoUtf8").GetString().ShouldBe(IdentityKdfProfiles.MasterKeyInfo);
        WebEncoders.Base64UrlEncode(accountRoot).ShouldBe(expected.GetProperty("accountRootBase64Url").GetString());
        WebEncoders.Base64UrlEncode(auth).ShouldBe(expected.GetProperty("authCredentialBase64Url").GetString());
        WebEncoders.Base64UrlEncode(masterKey).ShouldBe(expected.GetProperty("masterKeyBase64Url").GetString());
    }

    [Fact]
    public void PasswordOnlyBootstrap_ExposesOnlyFrozenPublicParameters()
    {
        typeof(LoginSaltResponse)
            .GetProperties()
            .Select(property => property.Name)
            .ShouldBe(
            [
                "AccountId",
                "ProfileId",
                "SecurityVersion",
                "KdfSalt",
                "MemoryKiB",
                "Iterations",
                "Parallelism",
            ]);
    }
}
