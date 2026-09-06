namespace ArgonComplexTest.Infrastructure.Account;

using System.Collections.Concurrent;
using Argon.Features.Testing;

/// <summary>One outgoing message, as the sink saw it.</summary>
/// <param name="To">Recipient address, lower-cased on the way in so a test can match on it.</param>
/// <param name="Kind">The stable template identifier — see <see cref="EmailKinds"/>.</param>
/// <param name="Subject">The subject line the send would have carried.</param>
/// <param name="Body">The rendered HTML body.</param>
/// <param name="At">When the grain decided to send it.</param>
public sealed record SentEmail(string To, string Kind, string Subject, string Body, DateTimeOffset At);

/// <summary>
/// Every e-mail the host decided to send, kept in memory for the fixtures to assert on.
/// </summary>
/// <remarks>
/// <para>Registered as a singleton on the test host and injected into <c>EmailManager</c> through the
/// optional <see cref="IEmailSink"/> parameter — the same shape <c>ITestCodeStore</c> already uses,
/// and absent in production. Without it the account-lifecycle mails are unobservable: there is no
/// SMTP server in the suite and the addresses are under <c>.local</c>, so every send is dropped after
/// a log line and "the reminder went out exactly once" has nothing to assert against.</para>
///
/// <para><b>Shared by the whole assembly</b>, because the host is. Fixtures run in parallel, so this
/// never filters by "the most recent mail" or by count alone — every query is by recipient address,
/// and the suite's addresses are unique per registration. It is also never cleared: clearing a
/// singleton another fixture is mid-assertion on is a race, and the memory is a few hundred short
/// strings.</para>
///
/// <para><b>On the compressed clocks two reminders are indistinguishable by content.</b> The reminder
/// template renders a whole number of days and both test thresholds — six seconds and three — floor
/// to zero, so both mails read "Account Deletion in 0 Day(s)". That is not a defect being papered
/// over: the grain's own bookkeeping keys on the threshold, not on the rendered text, so what
/// distinguishes them is arrival order and count. <see cref="WaitForCountAsync"/> exists for exactly
/// that assertion.</para>
/// </remarks>
public sealed class RecordingEmailSink : IEmailSink
{
    private readonly ConcurrentQueue<SentEmail> sent = new();

    public void Record(string to, string kind, string subject, string body)
        => sent.Enqueue(new SentEmail(to.ToLowerInvariant(), kind, subject, body, DateTimeOffset.UtcNow));

    /// <summary>Everything sent to one address, oldest first.</summary>
    public IReadOnlyList<SentEmail> Sent(string to)
    {
        var address = to.ToLowerInvariant();

        return sent.Where(mail => mail.To == address).ToArray();
    }

    /// <summary>Everything of one kind sent to one address, oldest first.</summary>
    public IReadOnlyList<SentEmail> Sent(string to, string kind)
        => Sent(to).Where(mail => mail.Kind == kind).ToArray();

    /// <summary>
    /// Waits for the first mail of a kind to reach one address, and returns it.
    /// </summary>
    /// <remarks>
    /// A poll with a deadline rather than a fixed wait: the send is a one-way grain call the caller
    /// does not await, so its arrival is genuinely asynchronous and any fixed wait is either flaky or
    /// slow. Returns <see langword="null"/> on timeout so the caller can phrase its own failure — a
    /// missing deletion mail and a missing export mail want different sentences.
    /// </remarks>
    public async Task<SentEmail?> WaitForAsync(string to, string kind, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (Sent(to, kind) is [var first, ..])
                return first;

            await Task.Delay(50, ct);
        }

        return Sent(to, kind).FirstOrDefault();
    }

    /// <summary>
    /// Waits until at least <paramref name="count"/> mails of a kind have reached one address.
    /// </summary>
    /// <remarks>
    /// Returns whatever it has when the deadline passes rather than throwing, so the caller asserts
    /// on the list — "two reminders" and "three reminders" are both interesting failures and a
    /// timeout exception would report neither.
    /// </remarks>
    public async Task<IReadOnlyList<SentEmail>> WaitForCountAsync(
        string to, string kind, int count, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var found = Sent(to, kind);

            if (found.Count >= count)
                return found;

            await Task.Delay(50, ct);
        }

        return Sent(to, kind);
    }
}
