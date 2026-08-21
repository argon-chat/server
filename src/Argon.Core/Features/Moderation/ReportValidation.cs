namespace Argon.Features.Moderation;

using ArgonContracts;

public enum ScoreDimension
{
    Content,
    Social,
    Commercial,
    Nuisance
}

public static class ReportValidation
{
    private static readonly Dictionary<ReportCategory, ReportReason[]> ValidReasons = new()
    {
        [ReportCategory.I_DONT_LIKE_IT] = [ReportReason.NONE],
        [ReportCategory.CHILD_ABUSE] = [ReportReason.CHILD_SEXUAL_ABUSE, ReportReason.CHILD_PHYSICAL_ABUSE],
        [ReportCategory.VIOLENCE] =
        [
            ReportReason.INSULTS_OR_FALSE_INFO, ReportReason.GRAPHIC_OR_DISTURBING_CONTENT,
            ReportReason.EXTREME_VIOLENCE, ReportReason.HATE_SPEECH_OR_SYMBOL,
            ReportReason.CALLING_FOR_VIOLENCE, ReportReason.ORGANIZED_CRIME,
            ReportReason.TERRORISM, ReportReason.ANIMAL_ABUSE
        ],
        [ReportCategory.ILLEGAL_GOODS] =
        [
            ReportReason.WEAPONS, ReportReason.DRUGS, ReportReason.FAKE_DOCUMENTS,
            ReportReason.COUNTERFEIT_MONEY, ReportReason.HACKING_TOOLS_AND_MALWARE,
            ReportReason.COUNTERFEIT_MERCHANDISE, ReportReason.OTHER_GOODS_AND_SERVICES
        ],
        [ReportCategory.ILLEGAL_ADULT_CONTENT] =
        [
            ReportReason.IAC_CHILD_ABUSE, ReportReason.ILLEGAL_SEXUAL_SERVICES,
            ReportReason.IAC_ANIMAL_ABUSE, ReportReason.NON_CONSENSUAL_SEXUAL_IMAGERY,
            ReportReason.PORNOGRAPHY, ReportReason.IAC_OTHER
        ],
        [ReportCategory.PERSONAL_DATA] =
        [
            ReportReason.PRIVATE_IMAGES, ReportReason.PHONE_NUMBER, ReportReason.ADDRESS,
            ReportReason.STOLEN_DATA_OR_CREDENTIALS, ReportReason.PD_OTHER
        ],
        [ReportCategory.SCAM_OR_FRAUD] =
        [
            ReportReason.IMPERSONATION, ReportReason.DECEPTIVE_FINANCIAL_CLAIMS,
            ReportReason.SF_MALWARE, ReportReason.PHISHING, ReportReason.FRAUDULENT_SELLER
        ],
        [ReportCategory.COPYRIGHT] = [ReportReason.NONE],
        [ReportCategory.SPAM] =
        [
            ReportReason.SPAM_INSULTS_OR_FALSE_INFO, ReportReason.PROMOTING_ILLEGAL_CONTENT,
            ReportReason.SPAM_OTHER
        ],
        [ReportCategory.OTHER] =
        [
            ReportReason.OTHER_I_DONT_LIKE_IT, ReportReason.OTHER_FALSE_INFO,
            ReportReason.OTHER_ILLEGAL_ADULT_CONTENT, ReportReason.OTHER_ILLEGAL_GOODS_AND_SERVICES,
            ReportReason.OTHER_ELSE
        ],
    };

    private static readonly Dictionary<ReportCategory, ScoreDimension> CategoryDimensions = new()
    {
        [ReportCategory.CHILD_ABUSE]           = ScoreDimension.Content,
        [ReportCategory.ILLEGAL_ADULT_CONTENT] = ScoreDimension.Content,
        [ReportCategory.COPYRIGHT]             = ScoreDimension.Content,
        [ReportCategory.VIOLENCE]              = ScoreDimension.Social,
        [ReportCategory.PERSONAL_DATA]         = ScoreDimension.Social,
        [ReportCategory.ILLEGAL_GOODS]         = ScoreDimension.Commercial,
        [ReportCategory.SCAM_OR_FRAUD]         = ScoreDimension.Commercial,
        [ReportCategory.SPAM]                  = ScoreDimension.Nuisance,
        [ReportCategory.I_DONT_LIKE_IT]        = ScoreDimension.Nuisance,
        [ReportCategory.OTHER]                 = ScoreDimension.Nuisance,
    };

    public static bool IsValidReasonForCategory(ReportCategory category, ReportReason reason)
        => ValidReasons.TryGetValue(category, out var valid) && valid.Contains(reason);

    public static ScoreDimension GetCategoryDimension(ReportCategory category)
        => CategoryDimensions.GetValueOrDefault(category, ScoreDimension.Nuisance);
}