namespace Argon.Features.Logic;

public class AccountDeletionOptions
{
    public const string SectionName = "AccountDeletion";

    /// <summary>
    ///     Whether automatic deletion of inactive accounts is enabled.
    /// </summary>
    public bool AutoDeleteEnabled { get; set; }

    /// <summary>
    ///     Number of days to wait before executing account deletion after user request.
    /// </summary>
    public int GracePeriodDays { get; set; } = 30;

    /// <summary>
    ///     Days before execution at which to send reminder emails.
    ///     Sorted descending (e.g., [7, 1] means reminders at 7 days and 1 day before).
    /// </summary>
    public int[] ReminderDays { get; set; } = [7, 1];
}
