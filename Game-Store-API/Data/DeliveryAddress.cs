using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Game_Store_API.Data;

public sealed record DeliveryAddress(
    [Required, StringLength(100, MinimumLength = 2)] string Recipient,
    [Required, StringLength(200)] string Line1,
    [StringLength(200)] string? Line2,
    [Required, StringLength(20)] string PostalCode,
    [Required, StringLength(100)] string City,
    [Required, RegularExpression("[A-Z]{2}")] string CountryCode) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (new[] { Recipient, Line1, Line2, PostalCode, City, CountryCode }.Any(value => value?.Any(char.IsControl) == true))
            yield return new ValidationResult("Address fields cannot contain control characters.");
    }

    public string Protect(IDataProtectionProvider provider) => provider.CreateProtector("GameStore.OrderAddress.v1").Protect(
        JsonSerializer.Serialize(this with { Recipient = Recipient.Trim(), Line1 = Line1.Trim(), Line2 = Line2?.Trim(), PostalCode = PostalCode.Trim(), City = City.Trim() }));

    public static DeliveryAddress? Unprotect(string? payload, IDataProtectionProvider provider) => payload is null ? null :
        JsonSerializer.Deserialize<DeliveryAddress>(provider.CreateProtector("GameStore.OrderAddress.v1").Unprotect(payload))
        ?? throw new InvalidOperationException("Invalid order delivery address.");
}
