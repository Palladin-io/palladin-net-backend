using Palladin.Module.Notification.Domain;
using Palladin.Module.Notification.Infrastructure.Email.Events;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Notification;

public sealed class SesEventParserTests
{
    [Fact]
    public void When_PermanentBounce_Then_SuppressesRecipientsAsHardBounce()
    {
        // Given
        const string json = """
        {
          "eventType": "Bounce",
          "mail": { "messageId": "0100-msg", "destination": ["hard@example.com"] },
          "bounce": {
            "bounceType": "Permanent",
            "bounceSubType": "General",
            "bouncedRecipients": [{ "emailAddress": "hard@example.com" }]
          }
        }
        """;

        // When
        var result = SesEventParser.Parse(json);

        // Then
        result.Kind.ShouldBe("Bounce/Permanent");
        result.MessageId.ShouldBe("0100-msg");
        result.Suppressions.ShouldHaveSingleItem();
        result.Suppressions[0].Address.ShouldBe("hard@example.com");
        result.Suppressions[0].Reason.ShouldBe(SuppressionReason.HardBounce);
    }

    [Fact]
    public void When_TransientBounce_Then_DoesNotSuppress()
    {
        // Given
        const string json = """
        {
          "eventType": "Bounce",
          "mail": { "messageId": "m2" },
          "bounce": {
            "bounceType": "Transient",
            "bouncedRecipients": [{ "emailAddress": "soft@example.com" }]
          }
        }
        """;

        // When
        var result = SesEventParser.Parse(json);

        // Then
        result.Kind.ShouldBe("Bounce/Transient");
        result.Suppressions.ShouldBeEmpty();
    }

    [Fact]
    public void When_Complaint_Then_SuppressesAllComplainedRecipients()
    {
        // Given — legacy notificationType shape with two recipients
        const string json = """
        {
          "notificationType": "Complaint",
          "mail": { "messageId": "m3" },
          "complaint": {
            "complainedRecipients": [
              { "emailAddress": "a@example.com" },
              { "emailAddress": "b@example.com" }
            ],
            "complaintFeedbackType": "abuse"
          }
        }
        """;

        // When
        var result = SesEventParser.Parse(json);

        // Then
        result.Kind.ShouldBe("Complaint");
        result.Suppressions.Count.ShouldBe(2);
        result.Suppressions.ShouldAllBe(s => s.Reason == SuppressionReason.Complaint);
        result.Suppressions.Select(s => s.Address).ShouldBe(["a@example.com", "b@example.com"]);
    }

    [Fact]
    public void When_Delivery_Then_NoSuppression()
    {
        // Given
        const string json = """
        { "eventType": "Delivery", "mail": { "messageId": "m4" }, "delivery": { "recipients": ["ok@example.com"] } }
        """;

        // When
        var result = SesEventParser.Parse(json);

        // Then
        result.Kind.ShouldBe("Delivery");
        result.Suppressions.ShouldBeEmpty();
    }

    [Fact]
    public void When_WrappedInSnsEnvelope_Then_UnwrapsAndFlagsEnveloped()
    {
        // Given — RawMessageDelivery disabled: the SES event hides inside the SNS envelope's Message
        const string json = """
        {
          "Type": "Notification",
          "MessageId": "sns-envelope-id",
          "Message": "{\"eventType\":\"Bounce\",\"mail\":{\"messageId\":\"m5\"},\"bounce\":{\"bounceType\":\"Permanent\",\"bouncedRecipients\":[{\"emailAddress\":\"hard@example.com\"}]}}"
        }
        """;

        // When
        var result = SesEventParser.Parse(json);

        // Then
        result.Enveloped.ShouldBeTrue();
        result.Kind.ShouldBe("Bounce/Permanent");
        result.MessageId.ShouldBe("m5");
        result.Suppressions.ShouldHaveSingleItem();
        result.Suppressions[0].Address.ShouldBe("hard@example.com");
    }
}
