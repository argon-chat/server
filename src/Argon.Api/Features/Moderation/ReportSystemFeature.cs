namespace Argon.Features.Moderation;

public static class ReportSystemFeature
{
    /// <summary>
    /// Nothing to register. Both sections are bound by the feature that declares them, and both
    /// options classes carry their own rule — the two <c>IValidateOptions</c> registrations that used
    /// to live here said the same thing a second time and only at startup.
    /// </summary>
    public static void AddReportSystem(this WebApplicationBuilder builder)
    {
    }
}
