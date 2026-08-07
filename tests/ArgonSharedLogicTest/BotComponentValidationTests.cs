namespace ArgonSharedLogicTest;

using Argon.Features.BotApi;

/// <summary>
/// Validation for the interactive components bots attach to messages: buttons, select menus and
/// modals. This is the boundary where a third-party bot's payload becomes something the client has
/// to render — every rule here exists so a malformed or hostile payload is rejected at the API
/// rather than shipped to every member of a space.
/// </summary>
[TestFixture]
public class BotComponentValidationTests
{
    private static BotControlV1 CallbackButton(string id = "confirm", string label = "Confirm") => new()
    {
        Type    = ControlType.Button,
        Variant = ButtonVariant.Callback,
        Label   = label,
        Id      = id
    };

    private static BotControlV1 LinkButton(string url = "https://argon.gl") => new()
    {
        Type    = ControlType.Button,
        Variant = ButtonVariant.Link,
        Label   = "Open",
        Url     = url
    };

    private static BotControlV1 StringSelect(string customId = "pick", params string[] values) => new()
    {
        Type     = ControlType.StringSelect,
        CustomId = customId,
        Options  = (values.Length == 0 ? ["a"] : values)
           .Select(v => new SelectOptionV1 { Label = v, Value = v })
           .ToList()
    };

    // ── OklchColor ──────────────────────────────────────────────────────────────────────────────

    [Test]
    public void OklchColor_InsideTheAllowedGamut_Validates()
        => Assert.DoesNotThrow(() => new OklchColor(0.6f, 0.2f, 180f).Validate());

    [Test]
    public void OklchColor_OutOfRangeLightness_Throws(
        [Values(0.39f, 0.81f)] float lightness)
        // The lightness band is clamped so bot-chosen colours stay legible on both themes.
        => Assert.Throws<ArgumentOutOfRangeException>(() => new OklchColor(lightness, 0.2f, 180f).Validate());

    [Test]
    public void OklchColor_OutOfRangeChroma_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new OklchColor(0.6f, 0.5f, 180f).Validate());

    [Test]
    public void OklchColor_HueIsHalfOpenAtThreeSixty_Throws()
        // 360 and 0 are the same hue; accepting both would let two payloads differ meaninglessly.
        => Assert.Throws<ArgumentOutOfRangeException>(() => new OklchColor(0.6f, 0.2f, 360f).Validate());

    // ── Buttons ─────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void CallbackButton_WithAnId_Validates()
        => Assert.DoesNotThrow(() => CallbackButton().Validate());

    [Test]
    public void CallbackButton_WithoutAnId_Throws()
        => Assert.Throws<ArgumentException>(() => (CallbackButton() with { Id = null }).Validate());

    [Test]
    public void CallbackButton_WithAUrl_Throws()
        => Assert.Throws<ArgumentException>(() => (CallbackButton() with { Url = "https://argon.gl" }).Validate());

    [Test]
    public void LinkButton_WithAnHttpsUrl_Validates()
        => Assert.DoesNotThrow(() => LinkButton().Validate());

    [Test]
    public void LinkButton_WithANonHttpScheme_Throws()
        // javascript: and friends would otherwise be handed straight to the client.
        => Assert.Throws<ArgumentException>(() => LinkButton("javascript:alert(1)").Validate());

    [Test]
    public void LinkButton_WithARelativeUrl_Throws()
        => Assert.Throws<ArgumentException>(() => LinkButton("/relative/path").Validate());

    [Test]
    public void LinkButton_WithAnId_Throws()
        => Assert.Throws<ArgumentException>(() => (LinkButton() with { Id = "nope" }).Validate());

    [Test]
    public void Button_WithoutAVariant_Throws()
        => Assert.Throws<ArgumentException>(() => (CallbackButton() with { Variant = null }).Validate());

    [Test]
    public void Button_WithAnOverlongLabel_Throws()
        => Assert.Throws<ArgumentException>(() => (CallbackButton() with { Label = new string('x', 81) }).Validate());

    [Test]
    public void Button_CarryingSelectFields_Throws()
        => Assert.Throws<ArgumentException>(() => (CallbackButton() with { CustomId = "not-for-buttons" }).Validate());

    [Test]
    public void Button_WithAnOutOfGamutColour_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => (CallbackButton() with { Colour = new OklchColor(0.1f, 0.2f, 10f) }).Validate());

    // ── Selects ─────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void StringSelect_WithOptions_Validates()
        => Assert.DoesNotThrow(() => StringSelect("pick", "one", "two").Validate());

    [Test]
    public void StringSelect_WithoutOptions_Throws()
        => Assert.Throws<ArgumentException>(() => (StringSelect() with { Options = [] }).Validate());

    [Test]
    public void StringSelect_WithMoreThanTwentyFiveOptions_Throws()
    {
        var options = Enumerable.Range(0, 26)
           .Select(i => new SelectOptionV1 { Label = $"o{i}", Value = $"v{i}" })
           .ToList();

        Assert.Throws<ArgumentException>(() => (StringSelect() with { Options = options }).Validate());
    }

    [Test]
    public void StringSelect_WithDuplicateOptionValues_Throws()
        // The submitted value is the only thing the bot gets back; duplicates make it ambiguous.
        => Assert.Throws<ArgumentException>(() => StringSelect("pick", "same", "same").Validate());

    [Test]
    public void Select_WithoutACustomId_Throws()
        => Assert.Throws<ArgumentException>(() => (StringSelect() with { CustomId = " " }).Validate());

    [Test]
    public void Select_WithMinAboveMax_Throws()
        => Assert.Throws<ArgumentException>(
            () => (StringSelect("pick", "a", "b") with { MinValues = 2, MaxValues = 1 }).Validate());

    [Test]
    public void Select_CarryingButtonFields_Throws()
        => Assert.Throws<ArgumentException>(() => (StringSelect() with { Label = "not-for-selects" }).Validate());

    [Test]
    public void EntitySelect_WithOptions_Throws()
    {
        // User/Archetype/Channel selects are populated by the client from live server state; letting
        // a bot supply options would let it fabricate members or channels that do not exist.
        var control = new BotControlV1
        {
            Type     = ControlType.UserSelect,
            CustomId = "pick-user",
            Options  = [new SelectOptionV1 { Label = "fake", Value = "fake" }]
        };

        Assert.Throws<ArgumentException>(() => control.Validate());
    }

    [Test]
    public void EntitySelect_WithoutOptions_Validates()
        => Assert.DoesNotThrow(() => new BotControlV1
        {
            Type     = ControlType.ChannelSelect,
            CustomId = "pick-channel"
        }.Validate());

    [Test]
    public void GetInteractionId_ReturnsIdForButtonsAndCustomIdForSelects()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CallbackButton("btn").GetInteractionId(), Is.EqualTo("btn"));
            Assert.That(StringSelect("sel", "a").GetInteractionId(), Is.EqualTo("sel"));
        });
    }

    // ── Rows ────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Row_OfButtons_Validates()
        => Assert.DoesNotThrow(() => new ControlRowV1([CallbackButton("a"), CallbackButton("b")]).Validate());

    [Test]
    public void Row_MixingButtonsAndASelect_Throws()
        => Assert.Throws<ArgumentException>(
            () => new ControlRowV1([CallbackButton("a"), StringSelect("s", "x")]).Validate());

    [Test]
    public void Row_WithMoreThanOneSelect_Throws()
        => Assert.Throws<ArgumentException>(
            () => new ControlRowV1([StringSelect("s1", "x"), StringSelect("s2", "y")]).Validate());

    [Test]
    public void Row_WithNoControls_Throws()
        => Assert.Throws<ArgumentException>(() => new ControlRowV1([]).Validate());

    [Test]
    public void Row_WithSixButtons_Throws()
        => Assert.Throws<ArgumentException>(() => new ControlRowV1(
            Enumerable.Range(0, 6).Select(i => CallbackButton($"b{i}")).ToList()).Validate());

    [Test]
    public void Row_WithDuplicateIdentifiers_Throws()
        => Assert.Throws<ArgumentException>(
            () => new ControlRowV1([CallbackButton("same"), CallbackButton("same")]).Validate());

    [Test]
    public void ValidateRows_AcceptsNullAndEmpty()
    {
        Assert.Multiple(() =>
        {
            Assert.DoesNotThrow(() => ControlRowV1.ValidateRows(null));
            Assert.DoesNotThrow(() => ControlRowV1.ValidateRows([]));
        });
    }

    [Test]
    public void ValidateRows_WithMoreThanFiveRows_Throws()
        => Assert.Throws<ArgumentException>(() => ControlRowV1.ValidateRows(
            Enumerable.Range(0, 6).Select(i => new ControlRowV1([CallbackButton($"b{i}")])).ToList()));

    [Test]
    public void ValidateRows_WithAnIdentifierDuplicatedAcrossRows_Throws()
        // Uniqueness has to hold message-wide, not just per row — the interaction event carries only
        // the identifier, so a collision across rows makes the bot's handler ambiguous.
        => Assert.Throws<ArgumentException>(() => ControlRowV1.ValidateRows(
        [
            new ControlRowV1([CallbackButton("shared")]),
            new ControlRowV1([CallbackButton("shared")])
        ]));

    [Test]
    public void ValidateRows_WithFiveFullRows_Validates()
        => Assert.DoesNotThrow(() => ControlRowV1.ValidateRows(
            Enumerable.Range(0, 5)
               .Select(r => new ControlRowV1(
                    Enumerable.Range(0, 5).Select(c => CallbackButton($"b{r}_{c}")).ToList()))
               .ToList()));

    // ── Modals ──────────────────────────────────────────────────────────────────────────────────

    private static ModalComponentV1 TextInput(string customId = "name") => new()
    {
        Type     = ModalComponentType.TextInput,
        CustomId = customId,
        Label    = "Your name",
        Style    = TextInputStyle.Short
    };

    private static ModalDefinitionV1 Modal(params ModalComponentV1[] components) => new()
    {
        CustomId   = "profile",
        Title      = "Edit profile",
        Components = components.Length == 0 ? [TextInput()] : components.ToList()
    };

    [Test]
    public void Modal_WithATextInput_Validates()
        => Assert.DoesNotThrow(() => Modal().Validate());

    [Test]
    public void Modal_WithNoComponents_Throws()
        => Assert.Throws<ArgumentException>(() => (Modal() with { Components = [] }).Validate());

    [Test]
    public void Modal_WithMoreThanFiveComponents_Throws()
        => Assert.Throws<ArgumentException>(() => (Modal() with
        {
            Components = Enumerable.Range(0, 6).Select(i => TextInput($"c{i}")).ToList()
        }).Validate());

    [Test]
    public void Modal_WithDuplicateComponentIds_Throws()
        => Assert.Throws<ArgumentException>(() => Modal(TextInput("same"), TextInput("same")).Validate());

    [Test]
    public void Modal_WithAnOverlongTitle_Throws()
        => Assert.Throws<ArgumentException>(() => (Modal() with { Title = new string('t', 46) }).Validate());

    [Test]
    public void ModalTextInput_WithoutAStyle_Throws()
        => Assert.Throws<ArgumentException>(() => (TextInput() with { Style = null }).Validate());

    [Test]
    public void ModalTextInput_WithAnOutOfRangeLength_Throws()
        => Assert.Throws<ArgumentException>(() => (TextInput() with { MaxLength = 4001 }).Validate());

    [Test]
    public void ModalComponent_WithAnOverlongLabel_Throws()
        => Assert.Throws<ArgumentException>(() => (TextInput() with { Label = new string('l', 46) }).Validate());

    [Test]
    public void ModalStringSelect_WithoutOptions_Throws()
        => Assert.Throws<ArgumentException>(() => new ModalComponentV1
        {
            Type     = ModalComponentType.StringSelect,
            CustomId = "pick",
            Label    = "Pick one"
        }.Validate());

    [Test]
    public void ModalCheckbox_NeedsNothingExtra()
        => Assert.DoesNotThrow(() => new ModalComponentV1
        {
            Type     = ModalComponentType.Checkbox,
            CustomId = "agree",
            Label    = "I agree"
        }.Validate());
}
