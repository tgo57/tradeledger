using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Reflection.Emit;
using TradeLedger.Core.Models;

namespace TradeLedger.Data;

public sealed class AppDbContext : DbContext
{
    public DbSet<Execution> Executions => Set<Execution>();

    public DbSet<TradeGroup> TradeGroups => Set<TradeGroup>();
    public DbSet<TradeGroupExecution> TradeGroupExecutions => Set<TradeGroupExecution>();

    public DbSet<TradeGroupLeg> TradeGroupLegs => Set<TradeGroupLeg>();


    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Execution>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.Broker, x.Account, x.ExecutedAt, x.Symbol });
            b.Property(x => x.Description).HasMaxLength(2048);
            b.Property(x => x.RawRowJson).HasColumnType("TEXT");
            b.Property(x => x.Fingerprint).HasMaxLength(128);
            b.HasIndex(x => x.Fingerprint).IsUnique();
            b.Property(x => x.Quantity).HasColumnType("REAL");
            b.Property(x => x.Price).HasColumnType("REAL");
            b.Property(x => x.Fees).HasColumnType("REAL");
            b.Property(x => x.NetAmount).HasColumnType("REAL");

        });

        modelBuilder.Entity<TradeGroupExecution>()
            .HasKey(x => new { x.TradeGroupId, x.ExecutionId });

        modelBuilder.Entity<TradeGroupExecution>()
            .HasOne(x => x.TradeGroup)
            .WithMany()
            .HasForeignKey(x => x.TradeGroupId);

        modelBuilder.Entity<TradeGroupExecution>()
            .HasOne(x => x.Execution)
            .WithMany()
            .HasForeignKey(x => x.ExecutionId);

        // Optional but helpful: prevent exact duplicate groups
        modelBuilder.Entity<TradeGroup>()
            .HasIndex(x => new { x.Broker, x.Account, x.Underlying, x.Expiration, x.Right, x.ShortStrike, x.LongStrike, x.OpenDate })
            .IsUnique();

        modelBuilder.Entity<TradeGroupLeg>(b =>
        {
            b.HasKey(x => x.Id);

            b.Property(x => x.Underlying).HasMaxLength(32);
            b.Property(x => x.Right).HasMaxLength(8);
            b.Property(x => x.Role).HasMaxLength(32);

            b.HasOne(x => x.TradeGroup)
             .WithMany() // or WithMany(g => g.Legs) if you add navigation
             .HasForeignKey(x => x.TradeGroupId)
             .OnDelete(DeleteBehavior.Cascade);
        });


    }
}
