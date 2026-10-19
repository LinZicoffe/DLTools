namespace 烧录上位机程序.Models.Authorization;

/// <summary>Display metadata only. Secret material never reaches page bindings.</summary>
public sealed record AuthorizationProfile(string DeviceId, string DisplayName, DateTimeOffset CreatedUtc);
