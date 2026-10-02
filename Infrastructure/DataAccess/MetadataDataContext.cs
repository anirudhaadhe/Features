using Microsoft.EntityFrameworkCore;
using Features.Infrastructure.DataAccess.Sample;

namespace Features.Infrastructure.DataAccess;

public class MetadataDataContext(DbContextOptions<MetadataDataContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(entity =>
        {
            entity.ToTable("Orders");
            entity.HasKey(order => order.Id);
            entity.Property(order => order.OrderDate).HasColumnType("date");
            entity.Property(order => order.Status).HasMaxLength(32).IsRequired();
            entity.Property(order => order.Total).HasPrecision(18, 2);
            entity.HasOne(order => order.Customer)
                .WithMany(customer => customer.Orders)
                .HasForeignKey(order => order.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Customer>(entity =>
        {
            entity.ToTable("Customers");
            entity.HasKey(customer => customer.Id);
            entity.Property(customer => customer.Name).HasMaxLength(128).IsRequired();
            entity.Property(customer => customer.Country).HasMaxLength(2).IsRequired();
            entity.Property(customer => customer.Email).HasMaxLength(256).IsRequired();
        });

        modelBuilder.Entity<OrderLine>(entity =>
        {
            entity.ToTable("OrderLines");
            entity.HasKey(line => line.Id);
            entity.HasOne(line => line.Order)
                .WithMany(order => order.Lines)
                .HasForeignKey(line => line.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
