using ChatApp.Api.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.Api.Data;

public class ChatDbContext(DbContextOptions<ChatDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<FriendRequest> FriendRequests => Set<FriendRequest>();

    public DbSet<Friendship> Friendships => Set<Friendship>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // datetime2 doesn't store DateTimeKind; every timestamp here is UTC, so restore that on read.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(user =>
        {
            user.Property(u => u.DisplayName).HasMaxLength(64);
        });

        builder.Entity<RefreshToken>(token =>
        {
            token.Property(t => t.TokenHash).HasMaxLength(64);
            token.HasIndex(t => t.TokenHash).IsUnique();
            token.HasIndex(t => t.UserId);
            token.HasOne(t => t.User)
                .WithMany()
                .HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<FriendRequest>(request =>
        {
            request.HasOne(r => r.Sender)
                .WithMany()
                .HasForeignKey(r => r.SenderId)
                .OnDelete(DeleteBehavior.Restrict);
            request.HasOne(r => r.Receiver)
                .WithMany()
                .HasForeignKey(r => r.ReceiverId)
                .OnDelete(DeleteBehavior.Restrict);

            // At most one pending request per sender/receiver pair; history rows are kept.
            request.HasIndex(r => new { r.SenderId, r.ReceiverId })
                .IsUnique()
                .HasFilter($"[{nameof(FriendRequest.Status)}] = {(int)FriendRequestStatus.Pending}");
            request.HasIndex(r => new { r.ReceiverId, r.Status });
        });

        builder.Entity<Friendship>(friendship =>
        {
            friendship.HasKey(f => new { f.UserId, f.FriendId });
            friendship.HasOne(f => f.User)
                .WithMany()
                .HasForeignKey(f => f.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            friendship.HasOne(f => f.Friend)
                .WithMany()
                .HasForeignKey(f => f.FriendId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
