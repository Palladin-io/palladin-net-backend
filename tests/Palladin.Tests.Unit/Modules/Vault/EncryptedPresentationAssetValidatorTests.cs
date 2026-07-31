using Microsoft.AspNetCore.WebUtilities;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Features;

namespace Palladin.Tests.Unit.Modules.Vault;

public sealed class EncryptedPresentationAssetValidatorTests
{
    [Fact]
    public void When_CiphertextExceedsFrozenLimit_Then_ValidationFails()
    {
        var validator = new UploadEncryptedPresentationAssetValidator();
        var request = new UploadEncryptedPresentationAssetRequest
        {
            VaultId = Guid.NewGuid(),
            AssetId = Guid.NewGuid(),
            Target = PresentationAssetTargetContract.Vault,
            MediaType = "image/png",
            Ciphertext = new byte[EncryptedPresentationAsset.MaximumCiphertextBytes + 1],
            CiphertextSha256 = WebEncoders.Base64UrlEncode(new byte[32]),
        };

        var result = validator.Validate(request);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(x => x.PropertyName == nameof(request.Ciphertext));
    }
}
