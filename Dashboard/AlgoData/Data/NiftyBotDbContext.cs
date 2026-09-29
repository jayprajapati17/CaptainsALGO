using AlgoData.Models;
using Microsoft.EntityFrameworkCore;

namespace AlgoData.Data;

public sealed class NiftyBotDbContext : DbContext
{
    public DbSet<PositionEntity> Positions => Set<PositionEntity>();
    public DbSet<SignalLogEntity> SignalLogs => Set<SignalLogEntity>();
    public DbSet<DailyCprEntity> DailyCprs => Set<DailyCprEntity>();
    public DbSet<AccessTokenEntity> AccessTokens => Set<AccessTokenEntity>();

    public NiftyBotDbContext(DbContextOptions<NiftyBotDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ---- Positions ----
        modelBuilder.Entity<PositionEntity>(e =>
        {
            e.ToTable("Positions");
            e.HasKey(p => p.Id);

            // Enum -> NVARCHAR text, values must match the CHECK constraints
            // in schema-sqlserver.sql exactly.
            e.Property(p => p.Strategy).HasConversion<string>().HasColumnName("Strategy").IsRequired();
            e.Property(p => p.Leg).HasConversion<string>().HasColumnName("Leg");
            e.Property(p => p.OptionType).HasConversion<string>().HasColumnName("OptionType").IsRequired();
            e.Property(p => p.Confidence).HasConversion<string>().HasColumnName("Confidence");
            e.Property(p => p.Status).HasConversion<string>().HasColumnName("Status").IsRequired();

            e.Property(p => p.Direction).HasColumnName("Direction").IsRequired();
            e.Property(p => p.InstrumentKey).HasColumnName("InstrumentKey").IsRequired();
            e.Property(p => p.TradingSymbol).HasColumnName("TradingSymbol").IsRequired();
            e.Property(p => p.Strike).HasColumnName("Strike").IsRequired();
            e.Property(p => p.Expiry).HasColumnName("Expiry").IsRequired();
            e.Property(p => p.LotSize).HasColumnName("LotSize").IsRequired();

            e.Property(p => p.SpotAtEntry).HasColumnName("SpotAtEntry").HasColumnType("decimal(18,2)").IsRequired();
            e.Property(p => p.EntryPremium).HasColumnName("EntryPremium").HasColumnType("decimal(18,2)").IsRequired();
            e.Property(p => p.EntryTime).HasColumnName("EntryTime").IsRequired();

            e.Property(p => p.AdxAtEntry).HasColumnName("AdxAtEntry");
            e.Property(p => p.AdxNCandlesAgo).HasColumnName("AdxNCandlesAgo");
            e.Property(p => p.BrokenLevel).HasColumnName("BrokenLevel").HasColumnType("decimal(18,2)");

            e.Property(p => p.LastKnownPremium).HasColumnName("LastKnownPremium").HasColumnType("decimal(18,2)").IsRequired();
            e.Property(p => p.LastUpdateTime).HasColumnName("LastUpdateTime").IsRequired();

            e.Property(p => p.ExitPremium).HasColumnName("ExitPremium").HasColumnType("decimal(18,2)");
            e.Property(p => p.ExitTime).HasColumnName("ExitTime");
            e.Property(p => p.ExitReason).HasColumnName("ExitReason");
            e.Property(p => p.FinalPnlRupees).HasColumnName("FinalPnlRupees").HasColumnType("decimal(18,2)");
            e.Property(p => p.FinalPnlPercent).HasColumnName("FinalPnlPercent");

            e.Property(p => p.CreatedAt).HasColumnName("CreatedAt").IsRequired();

            // Computed, not-mapped convenience properties.
            e.Ignore(p => p.LivePnlRupees);
            e.Ignore(p => p.LivePnlPercent);

            e.HasIndex(p => p.Status);
            e.HasIndex(p => p.EntryTime);
            e.HasIndex(p => p.Strategy);
        });

        // ---- SignalLog ----
        modelBuilder.Entity<SignalLogEntity>(e =>
        {
            e.ToTable("SignalLog"); // singular, matches schema-sqlserver.sql
            e.HasKey(s => s.Id);

            e.Property(s => s.Strategy).HasConversion<string>().IsRequired();
            e.Property(s => s.Confidence).HasConversion<string>();
            e.Property(s => s.Direction).IsRequired();
            e.Property(s => s.SignalTime).IsRequired();
            e.Property(s => s.SpotPrice).HasColumnType("decimal(18,2)").IsRequired();
            e.Property(s => s.BrokenLevel).HasColumnType("decimal(18,2)");

            e.HasIndex(s => s.SignalTime);
            e.HasIndex(s => s.Strategy);
        });

        // ---- DailyCpr ----
        modelBuilder.Entity<DailyCprEntity>(e =>
        {
            e.ToTable("DailyCpr");
            e.HasKey(c => c.Id);

            e.Property(c => c.Label).HasConversion<string>().IsRequired();
            e.Property(c => c.ForTradingDay).IsRequired();
            e.Property(c => c.Reading).IsRequired();
            e.Property(c => c.BiasNote).IsRequired();

            foreach (var moneyProp in new[] { nameof(DailyCprEntity.SourceHigh), nameof(DailyCprEntity.SourceLow),
                         nameof(DailyCprEntity.SourceClose), nameof(DailyCprEntity.CPRPivot), nameof(DailyCprEntity.Tc),
                         nameof(DailyCprEntity.Bc), nameof(DailyCprEntity.R1), nameof(DailyCprEntity.S1),
                         nameof(DailyCprEntity.R2), nameof(DailyCprEntity.S2) })
            {
                e.Property(moneyProp).HasColumnType("decimal(18,2)");
            }

            e.HasIndex(c => c.ForTradingDay);
            e.HasIndex(c => new { c.ForTradingDay, c.Label }).IsUnique();
        });

        // ---- AccessTokens ----
        modelBuilder.Entity<AccessTokenEntity>(e =>
        {
            e.ToTable("AccessTokens");
            e.HasKey(t => t.Id);

            e.Property(t => t.TokenDate).IsRequired();
            e.Property(t => t.Token).IsRequired();
            e.Property(t => t.GeneratedAt).IsRequired();

            e.HasIndex(t => t.TokenDate).IsUnique();
        });
    }
}