using EventWOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventWOS.Persistence.Configurations;

public sealed class RatingConfiguration : IEntityTypeConfiguration<Rating>
{
    public void Configure(EntityTypeBuilder<Rating> builder)
    {
        builder.ToTable("ratings");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id");

        builder.Property(r => r.EventId)      .HasColumnName("event_id").IsRequired();
        builder.Property(r => r.SubjectUserId).HasColumnName("subject_user_id").IsRequired();
        builder.Property(r => r.SubjectType)  .HasColumnName("subject_type").HasConversion<int>().IsRequired();
        builder.Property(r => r.RaterUserId)  .HasColumnName("rater_user_id").IsRequired();

        builder.Property(r => r.Performance).HasColumnName("performance").IsRequired();
        builder.Property(r => r.Cooperation).HasColumnName("cooperation").IsRequired();
        builder.Property(r => r.Comment)    .HasColumnName("comment").HasMaxLength(1000);

        // Provenance for crew ratings only; never part of uniqueness. See Rating.cs.
        builder.Property(r => r.AssignmentId).HasColumnName("assignment_id");

        builder.Property(r => r.RatedAt)   .HasColumnName("rated_at").IsRequired();
        builder.Property(r => r.RevisedAt) .HasColumnName("revised_at");
        builder.Property(r => r.IsLegacySingleScore)
               .HasColumnName("is_legacy_single_score").HasDefaultValue(false);

        // Score is derived from the two axes on read, so there is nothing to map.
        builder.Ignore(r => r.Score);

        // Audit
        builder.Property(r => r.CreatedAt).HasColumnName("created_at");
        builder.Property(r => r.CreatedBy).HasColumnName("created_by");
        builder.Property(r => r.UpdatedAt).HasColumnName("updated_at");
        builder.Property(r => r.UpdatedBy).HasColumnName("updated_by");
        builder.Property(r => r.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
        builder.Property(r => r.DeletedAt).HasColumnName("deleted_at");
        builder.Property(r => r.DeletedBy).HasColumnName("deleted_by");

        // Relationships. All Restrict: a rating is reputation history, so deleting
        // an event or a user must not silently erase the scores they earned or gave.
        builder.HasOne(r => r.Event)
               .WithMany()
               .HasForeignKey(r => r.EventId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.Subject)
               .WithMany()
               .HasForeignKey(r => r.SubjectUserId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.Rater)
               .WithMany()
               .HasForeignKey(r => r.RaterUserId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(r => r.EventId)      .HasDatabaseName("ix_ratings_event_id");
        builder.HasIndex(r => r.RaterUserId)  .HasDatabaseName("ix_ratings_rater_user_id");

        // Covers the query that actually matters: "average for this person as a
        // vendor / as crew", which is what every dashboard and user list reads.
        builder.HasIndex(r => new { r.SubjectUserId, r.SubjectType })
               .HasDatabaseName("ix_ratings_subject_user_id_subject_type");

        // ONE live rating per person per event PER CAPACITY. Filtered on is_deleted so a
        // withdrawn rating frees the slot for a fresh one instead of permanently
        // blocking it. Re-rating an already-rated person is a Revise, not a
        // second row -- the index is what makes that guarantee real rather than
        // merely intended, since two concurrent checkout requests would otherwise
        // both pass a "has this been rated?" read and each insert.
        builder.HasIndex(r => new { r.EventId, r.SubjectUserId, r.SubjectType })
               .IsUnique()
               .HasFilter("is_deleted = false")
               .HasDatabaseName("ux_ratings_event_subject_live");

        // Global soft-delete filter -- matches every other entity in the project.
        builder.HasQueryFilter(r => !r.IsDeleted);
    }
}
