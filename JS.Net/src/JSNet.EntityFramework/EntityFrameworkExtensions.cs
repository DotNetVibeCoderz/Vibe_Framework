using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace JSNet.EntityFramework;

/// <summary>JavaScript-style async query operators for Entity Framework Core queries.</summary>
public static class JsEntityFrameworkExtensions
{
    extension<T>(IQueryable<T> source)
    {
        public Task<List<T>> toListAsync(CancellationToken cancellationToken = default) => source.ToListAsync(cancellationToken);
        public Task<T[]> toArrayAsync(CancellationToken cancellationToken = default) => source.ToArrayAsync(cancellationToken);
        public Task<T?> firstOrDefaultAsync(CancellationToken cancellationToken = default) => source.FirstOrDefaultAsync(cancellationToken);
        public Task<T?> firstOrDefaultAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) => source.FirstOrDefaultAsync(predicate, cancellationToken);
        public Task<T> firstAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) => source.FirstAsync(predicate, cancellationToken);
        public Task<T> singleAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) => source.SingleAsync(predicate, cancellationToken);
        public Task<int> countAsync(CancellationToken cancellationToken = default) => source.CountAsync(cancellationToken);
        public Task<int> countAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) => source.CountAsync(predicate, cancellationToken);
        public Task<bool> anyAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) => source.AnyAsync(predicate, cancellationToken);
        public Task<double> sumAsync(Expression<Func<T, double>> selector, CancellationToken cancellationToken = default) => source.SumAsync(selector, cancellationToken);
        public Task<double> averageAsync(Expression<Func<T, double>> selector, CancellationToken cancellationToken = default) => source.AverageAsync(selector, cancellationToken);
    }

    extension<T>(IQueryable<T> source) where T : class
    {
        public IQueryable<T> include<TProperty>(Expression<Func<T, TProperty>> navigation) => source.Include(navigation);
        public IQueryable<T> asNoTracking() => source.AsNoTracking();
    }

    extension<T>(DbSet<T> set) where T : class
    {
        /// <summary>Adds an entity and returns it (chainable, like JavaScript ORMs).</summary>
        public T add(T entity)
        {
            set.Add(entity);
            return entity;
        }

        public void remove(T entity) => set.Remove(entity);
        public ValueTask<T?> findAsync(params object[] keys) => set.FindAsync(keys);
    }

    extension(DbContext context)
    {
        /// <summary><c>await db.save()</c> → <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>.</summary>
        public Task<int> save(CancellationToken cancellationToken = default) => context.SaveChangesAsync(cancellationToken);
        public Task<bool> ensureCreated(CancellationToken cancellationToken = default) => context.Database.EnsureCreatedAsync(cancellationToken);
    }
}
