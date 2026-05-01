namespace Argon.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record PaymentTransactionEntity : ArgonEntity, IEntityTypeConfiguration<PaymentTransactionEntity>
{
    public Guid   UserId          { get; set; }

    [MaxLength(64)]
    public string XsollaTxId      { get; set; } = null!;

    [MaxLength(32)]
    public string TransactionType { get; set; } = null!;   // subscription, boost_pack, gift

    [MaxLength(32)]
    public string? PlanExternalId { get; set; }             // ultima_monthly, ultima_annual, etc.

    [MaxLength(32)]
    public string? BoostPackType  { get; set; }             // Pack1, Pack3, Pack5

    public int?   BoostCount     { get; set; }

    [MaxLength(32)]
    public string? Amount        { get; set; }

    [MaxLength(8)]
    public string? Currency      { get; set; }

    public Guid? RecipientId    { get; set; }               // for gifts

    [MaxLength(4)]
    public string? CardSuffix   { get; set; }               // last 4 digits

    [MaxLength(32)]
    public string? CardBrand    { get; set; }               // Visa, Mastercard

    public long?  PaymentAccountId { get; set; }            // Xsolla saved payment account ID

    [MaxLength(16)]
    public string? Status        { get; set; }              // done, canceled, refunded

    public virtual UserEntity User { get; set; } = null!;

    public void Configure(EntityTypeBuilder<PaymentTransactionEntity> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.UserId).IsRequired();
        builder.Property(x => x.XsollaTxId).IsRequired();
        builder.Property(x => x.TransactionType).IsRequired();

        builder.HasIndex(x => x.UserId);
        builder.HasIndex(x => x.XsollaTxId).IsUnique();

        builder.HasOne(x => x.User)
           .WithMany()
           .HasForeignKey(x => x.UserId)
           .OnDelete(DeleteBehavior.Cascade);
    }
}
