namespace ArgonSharedLogicTest.Moderation;

using Argon.Features.Moderation;
using ArgonContracts;

/// <summary>
/// What a report may point at, before the database is asked anything.
/// </summary>
[TestFixture]
public class ReportTargetRulesTests
{
    private static readonly Guid Me    = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();
    private static readonly Guid Room  = Guid.NewGuid();

    [Test]
    public void Reporting_yourself_is_refused_by_name()
        => Assert.That(ReportTargetRules.Check(new ReportTarget(ReportTargetKind.USER, Me, null, null), Me),
            Is.EqualTo(SubmitReportError.CANNOT_REPORT_SELF));

    [Test]
    public void An_empty_id_is_not_a_target()
        => Assert.That(ReportTargetRules.Check(new ReportTarget(ReportTargetKind.USER, Guid.Empty, null, null), Me),
            Is.EqualTo(SubmitReportError.INVALID_TARGET));

    /// <summary>
    /// A value this schema revision does not declare arrives as a number that matches no member.
    /// </summary>
    [Test]
    public void An_unknown_kind_is_not_a_target()
        => Assert.That(ReportTargetRules.Check(new ReportTarget((ReportTargetKind)99, Other, null, null), Me),
            Is.EqualTo(SubmitReportError.INVALID_TARGET));

    [Test]
    public void A_user_profile_space_or_channel_carries_no_message([Values(
        ReportTargetKind.USER, ReportTargetKind.PROFILE, ReportTargetKind.SPACE, ReportTargetKind.CHANNEL)] ReportTargetKind kind)
    {
        Assert.Multiple(() =>
        {
            Assert.That(ReportTargetRules.Check(new ReportTarget(kind, Other, null, null), Me), Is.Null);
            Assert.That(ReportTargetRules.Check(new ReportTarget(kind, Other, null, 1), Me), Is.EqualTo(SubmitReportError.INVALID_TARGET));
        });
    }

    [Test]
    public void A_message_needs_its_channel_and_its_id()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ReportTargetRules.Check(new ReportTarget(ReportTargetKind.MESSAGE, Other, Room, 42), Me), Is.Null);
            Assert.That(ReportTargetRules.Check(new ReportTarget(ReportTargetKind.MESSAGE, Other, null, 42), Me), Is.EqualTo(SubmitReportError.INVALID_TARGET));
            Assert.That(ReportTargetRules.Check(new ReportTarget(ReportTargetKind.MESSAGE, Other, Guid.Empty, 42), Me), Is.EqualTo(SubmitReportError.INVALID_TARGET));
            Assert.That(ReportTargetRules.Check(new ReportTarget(ReportTargetKind.MESSAGE, Other, Room, null), Me), Is.EqualTo(SubmitReportError.INVALID_TARGET));
            Assert.That(ReportTargetRules.Check(new ReportTarget(ReportTargetKind.MESSAGE, Other, Room, 0), Me), Is.EqualTo(SubmitReportError.INVALID_TARGET));
        });
    }

    [Test]
    public void A_direct_message_names_the_peer_once()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ReportTargetRules.Check(new ReportTarget(ReportTargetKind.DIRECT_MESSAGE, Other, null, 7), Me), Is.Null);
            Assert.That(ReportTargetRules.Check(new ReportTarget(ReportTargetKind.DIRECT_MESSAGE, Other, Other, 7), Me), Is.Null,
                "the channel slot may repeat the peer, which is the shape older clients send");
            Assert.That(ReportTargetRules.Check(new ReportTarget(ReportTargetKind.DIRECT_MESSAGE, Other, Room, 7), Me), Is.EqualTo(SubmitReportError.INVALID_TARGET),
                "a channel that is not the peer is a contradiction");
            Assert.That(ReportTargetRules.Check(new ReportTarget(ReportTargetKind.DIRECT_MESSAGE, Other, null, null), Me), Is.EqualTo(SubmitReportError.INVALID_TARGET));
        });
    }

    [Test]
    public void Profile_and_user_are_one_kind()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ReportTargetRules.Canonical(ReportTargetKind.PROFILE), Is.EqualTo(ReportTargetKind.USER));
            Assert.That(ReportTargetRules.GroupKey(ReportTargetKind.PROFILE, Other, null, null, null),
                Is.EqualTo(ReportTargetRules.GroupKey(ReportTargetKind.USER, Other, null, null, null)));
        });
    }

    /// <summary>
    /// "The author of this message" and "this message" are the same complaint.
    /// </summary>
    [Test]
    public void A_message_is_keyed_by_where_it_is_not_by_who_wrote_it()
        => Assert.That(ReportTargetRules.GroupKey(ReportTargetKind.MESSAGE, Other, Room, null, 42),
            Is.EqualTo(ReportTargetRules.GroupKey(ReportTargetKind.MESSAGE, Guid.NewGuid(), Room, null, 42)));

    [Test]
    public void Different_things_get_different_keys()
    {
        var keys = new[]
        {
            ReportTargetRules.GroupKey(ReportTargetKind.USER, Other, null, null, null),
            ReportTargetRules.GroupKey(ReportTargetKind.SPACE, Other, null, null, null),
            ReportTargetRules.GroupKey(ReportTargetKind.CHANNEL, Other, null, null, null),
            ReportTargetRules.GroupKey(ReportTargetKind.MESSAGE, Other, Room, null, 1),
            ReportTargetRules.GroupKey(ReportTargetKind.MESSAGE, Other, Room, null, 2),
            ReportTargetRules.GroupKey(ReportTargetKind.DIRECT_MESSAGE, Other, null, Room, 1)
        };

        Assert.That(keys, Is.Unique);
    }

    [Test]
    public void Which_kinds_are_about_a_person_and_which_carry_content()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ReportTargetRules.TargetsAPerson(ReportTargetKind.USER), Is.True);
            Assert.That(ReportTargetRules.TargetsAPerson(ReportTargetKind.PROFILE), Is.True);
            Assert.That(ReportTargetRules.TargetsAPerson(ReportTargetKind.MESSAGE), Is.True, "the author");
            Assert.That(ReportTargetRules.TargetsAPerson(ReportTargetKind.DIRECT_MESSAGE), Is.True);
            Assert.That(ReportTargetRules.TargetsAPerson(ReportTargetKind.SPACE), Is.False);
            Assert.That(ReportTargetRules.TargetsAPerson(ReportTargetKind.CHANNEL), Is.False);

            Assert.That(ReportTargetRules.CarriesContent(ReportTargetKind.MESSAGE), Is.True);
            Assert.That(ReportTargetRules.CarriesContent(ReportTargetKind.DIRECT_MESSAGE), Is.True);
            Assert.That(ReportTargetRules.CarriesContent(ReportTargetKind.USER), Is.False);
            Assert.That(ReportTargetRules.CarriesContent(ReportTargetKind.SPACE), Is.False);
        });
    }
}
