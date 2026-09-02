namespace ArgonSharedLogicTest.Moderation;

using Argon.Features.Moderation;
using ArgonContracts;

/// <summary>
/// The moves a case may make. The one that matters most is the move it may not: a resolution does
/// not overwrite a resolution, which is how the old console let a second click rewrite the outcome
/// the trust scores had already been computed from.
/// </summary>
[TestFixture]
public class ReportCaseTransitionsTests
{
    private static readonly ReportStatus[] Open        = [ReportStatus.PENDING, ReportStatus.UNDER_REVIEW, ReportStatus.ESCALATED];
    private static readonly ReportStatus[] Resolutions = [ReportStatus.RESOLVED_ACTION_TAKEN, ReportStatus.RESOLVED_NO_ACTION, ReportStatus.DISMISSED];

    [Test]
    public void Every_status_is_either_open_or_a_resolution()
    {
        foreach (var status in Enum.GetValues<ReportStatus>())
            Assert.That(ReportCaseTransitions.IsOpen(status) ^ ReportCaseTransitions.IsResolution(status), Is.True,
                $"{status} must be exactly one of open or resolved");
    }

    [Test]
    public void An_open_case_can_be_resolved_any_way([ValueSource(nameof(Open))] ReportStatus from, [ValueSource(nameof(Resolutions))] ReportStatus to)
        => Assert.That(ReportCaseTransitions.CanResolve(from, to), Is.True);

    [Test]
    public void A_resolved_case_cannot_be_resolved_again([ValueSource(nameof(Resolutions))] ReportStatus from, [ValueSource(nameof(Resolutions))] ReportStatus to)
        => Assert.That(ReportCaseTransitions.CanResolve(from, to), Is.False);

    [Test]
    public void An_open_state_is_not_a_resolution([ValueSource(nameof(Open))] ReportStatus from, [ValueSource(nameof(Open))] ReportStatus to)
        => Assert.That(ReportCaseTransitions.CanResolve(from, to), Is.False,
            "setting a case back to PENDING through the resolve path is not resolving it");

    [Test]
    public void Only_open_cases_can_be_assigned()
    {
        Assert.Multiple(() =>
        {
            foreach (var status in Open)
                Assert.That(ReportCaseTransitions.CanAssign(status), Is.True, status.ToString());
            foreach (var status in Resolutions)
                Assert.That(ReportCaseTransitions.CanAssign(status), Is.False, status.ToString());
        });
    }

    [Test]
    public void Only_resolved_cases_can_be_reopened()
    {
        Assert.Multiple(() =>
        {
            foreach (var status in Resolutions)
                Assert.That(ReportCaseTransitions.CanReopen(status), Is.True, status.ToString());
            foreach (var status in Open)
                Assert.That(ReportCaseTransitions.CanReopen(status), Is.False, status.ToString());
        });
    }

    [Test]
    public void A_reopened_case_returns_to_the_open_state_its_flag_says()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ReportCaseTransitions.OpenStateFor(isEscalated: true), Is.EqualTo(ReportStatus.ESCALATED));
            Assert.That(ReportCaseTransitions.OpenStateFor(isEscalated: false), Is.EqualTo(ReportStatus.PENDING));
        });
    }
}
