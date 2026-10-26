using Microsoft.EntityFrameworkCore;

namespace LossRun.Data;

/// <summary>
/// EF Core mapping of the db/changelog schema (dbo only, ADR 0005/0006). No migrations: the changesets create the
/// tables, and <c>DeployTests</c> checks this model against them. Ledger tables carry hidden GENERATED ALWAYS columns
/// the model does not map. Their ids are hi/lo blocks of the dbo sequences (increment 100), allocated client-side:
/// append-only ledger tables reject the MERGE EF would use to read back server-generated keys.
/// </summary>
public sealed class LossRunDbContext(DbContextOptions<LossRunDbContext> options) : DbContext(options)
{
    public DbSet<UploadRow> Uploads => Set<UploadRow>();
    public DbSet<UploadItemRow> UploadItems => Set<UploadItemRow>();
    public DbSet<DocumentRow> Documents => Set<DocumentRow>();
    public DbSet<PolicyPeriodRow> PolicyPeriods => Set<PolicyPeriodRow>();
    public DbSet<ClaimRow> Claims => Set<ClaimRow>();
    public DbSet<ExtractedValueRow> ExtractedValues => Set<ExtractedValueRow>();
    public DbSet<OverrideRow> Overrides => Set<OverrideRow>();
    public DbSet<AuditEventRow> AuditEvents => Set<AuditEventRow>();
    public DbSet<RuleOutcomeRow> RuleOutcomes => Set<RuleOutcomeRow>();
    public DbSet<ReviewItemRow> ReviewItems => Set<ReviewItemRow>();
    public DbSet<FormatRow> Formats => Set<FormatRow>();
    public DbSet<FormatVersionRow> FormatVersions => Set<FormatVersionRow>();
    public DbSet<FormatFieldStatsRow> FormatFieldStats => Set<FormatFieldStatsRow>();
    public DbSet<UsageEventRow> UsageEvents => Set<UsageEventRow>();
    public DbSet<DocumentCostRow> DocumentCosts => Set<DocumentCostRow>();
    public DbSet<LedgerDigestRow> LedgerDigests => Set<LedgerDigestRow>();
    public DbSet<FeatureSnapshotRow> FeatureSnapshots => Set<FeatureSnapshotRow>();
    public DbSet<QuoteContextRow> QuoteContexts => Set<QuoteContextRow>();
    public DbSet<QuoteLabelRow> QuoteLabels => Set<QuoteLabelRow>();
    public DbSet<QuoteOwnerRow> QuoteOwners => Set<QuoteOwnerRow>();
    public DbSet<EscalationRow> Escalations => Set<EscalationRow>();
    public DbSet<GlossaryAliasRow> GlossaryAliases => Set<GlossaryAliasRow>();
    public DbSet<GlossaryAliasEvidenceRow> GlossaryAliasEvidence => Set<GlossaryAliasEvidenceRow>();
    public DbSet<AuditTrailRow> AuditTrail => Set<AuditTrailRow>();
    public DbSet<WorkspaceRow> Workspaces => Set<WorkspaceRow>();
    public DbSet<ChatTurnRow> ChatTurns => Set<ChatTurnRow>();
    public DbSet<OutboxRow> Outbox => Set<OutboxRow>();
    public DbSet<ScopedDocumentRow> ScopedDocuments => Set<ScopedDocumentRow>();

    public static DbContextOptions<LossRunDbContext> Options(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<LossRunDbContext>();
        if (SqlConnections.UsesEntra(connectionString))
        {
            // Serverless Azure SQL resumes from auto-pause with error 40613 for up to a minute: retry longer than the default.
            builder.UseSqlServer(SqlConnections.WithoutAuthentication(connectionString), sql => sql.EnableRetryOnFailure(8, TimeSpan.FromSeconds(15), SqlConnections.ResumeErrors))
                .AddInterceptors(new EntraTokenInterceptor());
        }
        else
        {
            builder.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(3));
        }

        return builder.Options;
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("dbo");
        foreach (var sequence in new[] { "ExtractedValueSeq", "OverrideSeq", "AuditEventSeq", "UsageEventSeq" })
        {
            b.HasSequence<long>(sequence, "dbo").IncrementsBy(100); // hi/lo block size; must match the changesets
        }

        b.Entity<UploadRow>(e =>
        {
            e.ToTable("Upload");
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.UploadId);
        });
        b.Entity<UploadItemRow>(e =>
        {
            e.ToTable("UploadItem");
            e.HasKey(x => x.DocumentId);
            e.Property(x => x.CostUsd).HasPrecision(18, 6);
        });
        b.Entity<DocumentRow>(e =>
        {
            e.ToTable("Document");
            e.Property(x => x.RowVersion).IsRowVersion();
            e.Property(x => x.CostUsd).HasPrecision(18, 6);
        });
        b.Entity<PolicyPeriodRow>(e => e.ToTable("PolicyPeriod"));
        b.Entity<ClaimRow>(e =>
        {
            e.ToTable("Claim", t => t.IsTemporal(h =>
            {
                h.HasPeriodStart("ValidFromUtc");
                h.HasPeriodEnd("ValidToUtc");
                h.UseHistoryTable("ClaimHistory", "dbo");
            }));
            foreach (var money in typeof(ClaimRow).GetProperties().Where(p => p.PropertyType == typeof(decimal?)))
            {
                e.Property(money.Name).HasPrecision(18, 2);
            }
        });
        b.Entity<ExtractedValueRow>(e =>
        {
            e.ToTable("ExtractedValue");
            e.Property(x => x.Id).UseHiLo("ExtractedValueSeq", "dbo");
        });
        b.Entity<OverrideRow>(e =>
        {
            e.ToTable("Override");
            e.Property(x => x.Id).UseHiLo("OverrideSeq", "dbo"); // assigned on Add, so audit rows and the claim can reference it
        });
        b.Entity<AuditEventRow>(e =>
        {
            e.ToTable("AuditEvent");
            e.Property(x => x.Id).UseHiLo("AuditEventSeq", "dbo");
        });
        b.Entity<RuleOutcomeRow>(e =>
        {
            e.ToTable("RuleOutcome");
            e.HasKey(x => new { x.DocumentId, x.DocumentRevision, x.Ordinal });
        });
        b.Entity<ReviewItemRow>(e =>
        {
            e.ToTable("ReviewItem");
            e.HasKey(x => x.DocumentId);
        });
        b.Entity<FormatRow>(e => e.ToTable("Format"));
        b.Entity<FormatVersionRow>(e =>
        {
            e.ToTable("FormatVersion");
            e.HasKey(x => new { x.FormatId, x.Version });
        });
        b.Entity<FormatFieldStatsRow>(e =>
        {
            e.ToTable("FormatFieldStats");
            e.HasKey(x => new { x.FormatId, x.FieldPath });
        });
        b.Entity<UsageEventRow>(e =>
        {
            e.ToTable("UsageEvent");
            e.Property(x => x.Id).UseHiLo("UsageEventSeq", "dbo");
            e.Property(x => x.Quantity).HasPrecision(19, 6);
        });
        b.Entity<DocumentCostRow>(e =>
        {
            e.ToTable("DocumentCost");
            e.HasKey(x => x.DocumentId);
            e.Property(x => x.CostUsd).HasPrecision(18, 6);
        });
        b.Entity<LedgerDigestRow>(e => e.ToTable("LedgerDigest"));
        b.Entity<FeatureSnapshotRow>(e =>
        {
            e.ToTable("FeatureSnapshot");
            e.Property(x => x.Id).UseIdentityColumn();
        });
        b.Entity<QuoteContextRow>(e =>
        {
            e.ToTable("QuoteContext", t => t.IsTemporal(h =>
            {
                h.HasPeriodStart("ValidFromUtc");
                h.HasPeriodEnd("ValidToUtc");
                h.UseHistoryTable("QuoteContextHistory", "dbo");
            }));
            e.HasKey(x => x.QuoteId);
        });
        b.Entity<EscalationRow>(e =>
        {
            e.ToTable("Escalation", t => t.IsTemporal(h =>
            {
                h.HasPeriodStart("ValidFromUtc");
                h.HasPeriodEnd("ValidToUtc");
                h.UseHistoryTable("EscalationHistory", "dbo");
            }));
            e.HasKey(x => x.Id);
            e.Property(x => x.Version).IsConcurrencyToken();
        });
        b.Entity<QuoteOwnerRow>(e =>
        {
            e.ToTable("QuoteOwner", t => t.IsTemporal(h =>
            {
                h.HasPeriodStart("ValidFromUtc");
                h.HasPeriodEnd("ValidToUtc");
                h.UseHistoryTable("QuoteOwnerHistory", "dbo");
            }));
            e.HasKey(x => x.QuoteId);
        });
        b.Entity<QuoteLabelRow>(e =>
        {
            e.ToTable("QuoteLabel", t => t.IsTemporal(h =>
            {
                h.HasPeriodStart("ValidFromUtc");
                h.HasPeriodEnd("ValidToUtc");
                h.UseHistoryTable("QuoteLabelHistory", "dbo");
            }));
            e.HasKey(x => x.QuoteId);
        });
        b.Entity<GlossaryAliasRow>(e =>
        {
            e.ToTable("GlossaryAlias", t => t.IsTemporal(h =>
            {
                h.HasPeriodStart("ValidFromUtc");
                h.HasPeriodEnd("ValidToUtc");
                h.UseHistoryTable("GlossaryAliasHistory", "dbo");
            }));
            e.HasKey(x => x.Id);
        });
        b.Entity<GlossaryAliasEvidenceRow>(e =>
        {
            e.ToTable("GlossaryAliasEvidence");
            e.HasKey(x => new { x.AliasId, x.DocumentId });
        });
        b.Entity<AuditTrailRow>(e => e.HasNoKey().ToView("vAuditTrail"));
        b.Entity<WorkspaceRow>(e => e.ToTable("Workspace"));
        b.Entity<OutboxRow>(e => e.ToTable("Outbox"));
        b.Entity<ChatTurnRow>(e =>
        {
            e.ToTable("ChatTurn");
            e.Property(x => x.CostUsd).HasPrecision(18, 6);
        });
        b.Entity<ScopedDocumentRow>(e => e.HasNoKey().ToView(null));

        // The changesets' foreign keys, declared so EF orders inserts parent-first (no navigations needed).
        b.Entity<DocumentRow>().HasOne<UploadRow>().WithMany().HasForeignKey(d => d.UploadId);
        b.Entity<PolicyPeriodRow>().HasOne<DocumentRow>().WithMany().HasForeignKey(p => p.DocumentId);
        b.Entity<ClaimRow>().HasOne<DocumentRow>().WithMany().HasForeignKey(c => c.DocumentId);
        b.Entity<ClaimRow>().HasOne<PolicyPeriodRow>().WithMany().HasForeignKey(c => c.PolicyPeriodId);
        b.Entity<RuleOutcomeRow>().HasOne<DocumentRow>().WithMany().HasForeignKey(r => r.DocumentId);
        b.Entity<FormatVersionRow>().HasOne<FormatRow>().WithMany().HasForeignKey(v => v.FormatId);
        b.Entity<FormatFieldStatsRow>().HasOne<FormatRow>().WithMany().HasForeignKey(f => f.FormatId);
    }
}
