namespace costats.Core.Pulse;

/// <summary>
/// A user-actionable alert attached to a provider reading. Drives prominent UI treatment
/// (e.g. a warning glyph and dimmed usage) beyond the informational <see cref="ReadingConfidence"/>.
/// </summary>
public enum ReadingAlert
{
    /// <summary>No alert; the reading is healthy.</summary>
    None = 0,

    /// <summary>The provider's login is expired/missing and the user must re-authenticate.</summary>
    SignInRequired = 1
}
