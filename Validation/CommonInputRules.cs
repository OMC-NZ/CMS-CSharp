using System.Globalization;
using System.Net.Mail;
using System.Text.RegularExpressions;

namespace CMS_CSharp.Validation;

internal static partial class CommonInputRules
{
    public static string NormalizeTitle(string value, string fieldName)
    {
        var normalized = WhitespaceRegex().Replace(value.Trim(), " ");
        EnsureRequiredAscii(normalized, fieldName);
        return CultureInfo.GetCultureInfo("en-NZ")
            .TextInfo
            .ToTitleCase(normalized.ToLowerInvariant());
    }

    public static string NormalizeEmail(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        EnsureRequiredAscii(normalized, "email");
        if (!MailAddress.TryCreate(normalized, out var emailAddress) ||
            !string.Equals(emailAddress.Address, normalized, StringComparison.OrdinalIgnoreCase))
        {
            throw new InputValidationException("email must be a valid email address.");
        }

        return normalized;
    }

    public static string NormalizeContact(string value)
    {
        var normalized = string.Concat(value.Where(character => !char.IsWhiteSpace(character)));
        EnsureRequiredAscii(normalized, "contact");
        if (normalized.Any(character => !char.IsAsciiDigit(character)))
        {
            throw new InputValidationException(
                "contact must contain digits only; spaces are removed automatically.");
        }

        return normalized;
    }

    public static string NormalizePostcode(string value)
    {
        var normalized = value.Trim();
        if (!PostcodeRegex().IsMatch(normalized))
        {
            throw new InputValidationException(
                "postcode must contain exactly four digits.");
        }

        return normalized;
    }

    public static string NormalizeDigits(
        string value,
        string fieldName,
        int exactLength)
    {
        var normalized = value.Trim();
        if (normalized.Length != exactLength ||
            normalized.Any(character => !char.IsAsciiDigit(character)))
        {
            throw new InputValidationException(
                $"{fieldName} must contain exactly {exactLength} digits.");
        }

        return normalized;
    }

    public static string NormalizeRequiredAscii(string value, string fieldName)
    {
        var normalized = value.Trim();
        EnsureRequiredAscii(normalized, fieldName);
        return normalized;
    }

    public static string? NormalizeOptionalAscii(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        EnsureAscii(normalized, fieldName);
        return normalized;
    }

    private static void EnsureRequiredAscii(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InputValidationException($"{fieldName} is required.");
        }

        EnsureAscii(value, fieldName);
    }

    private static void EnsureAscii(string value, string fieldName)
    {
        if (value.Any(character => !char.IsAscii(character) || char.IsControl(character)))
        {
            throw new InputValidationException(
                $"{fieldName} may contain ASCII English letters, digits, spaces, and symbols only.");
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^[0-9]{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex PostcodeRegex();
}
