namespace StealthDesk.Libraries.Branding.Dtos;

/// <summary>
/// Customization config for a white-label build.
/// All properties are optional; when null, <see cref="BrandingConstants"/> defaults are used.
/// </summary>
public record CustomizationConfigDto(
    string? BrandName = null,
    string? Publisher = null,
    string? Version = null,
    CustomizationColorsDto? Colors = null,
    CustomizationImagesDto? Images = null);
