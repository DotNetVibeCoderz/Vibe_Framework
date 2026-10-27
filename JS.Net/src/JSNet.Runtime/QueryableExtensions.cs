using System.Linq.Expressions;

namespace JSNet.Runtime;

/// <summary>
/// Lower-case LINQ on <see cref="IQueryable{T}"/>. These win over the <see cref="IEnumerable{T}"/> versions for queryables,
/// so lambdas become expression trees and providers such as Entity Framework translate them to SQL instead of loading
/// every row into memory.
/// </summary>
public static class JsQueryableExtensions
{
    extension<T>(IQueryable<T> source)
    {
        public IQueryable<T> where(Expression<Func<T, bool>> predicate) => source.Where(predicate);
        public IQueryable<TResult> select<TResult>(Expression<Func<T, TResult>> selector) => source.Select(selector);
        public IQueryable<TResult> selectMany<TResult>(Expression<Func<T, IEnumerable<TResult>>> selector) => source.SelectMany(selector);
        public IOrderedQueryable<T> orderBy<TKey>(Expression<Func<T, TKey>> key) => source.OrderBy(key);
        public IOrderedQueryable<T> orderByDescending<TKey>(Expression<Func<T, TKey>> key) => source.OrderByDescending(key);
        public IQueryable<IGrouping<TKey, T>> groupBy<TKey>(Expression<Func<T, TKey>> key) => source.GroupBy(key);
        public IQueryable<T> distinct() => source.Distinct();
        public IQueryable<T> take(int count) => source.Take(count);
        public IQueryable<T> skip(int count) => source.Skip(count);
        public T first() => source.First();
        public T first(Expression<Func<T, bool>> predicate) => source.First(predicate);
        public T? firstOrDefault() => source.FirstOrDefault();
        public T? firstOrDefault(Expression<Func<T, bool>> predicate) => source.FirstOrDefault(predicate);
        public int count() => source.Count();
        public int count(Expression<Func<T, bool>> predicate) => source.Count(predicate);
        public bool any() => source.Any();
        public bool any(Expression<Func<T, bool>> predicate) => source.Any(predicate);
        public bool all(Expression<Func<T, bool>> predicate) => source.All(predicate);
        public List<T> toList() => source.ToList();
        public T[] toArray() => source.ToArray();
    }

    extension<T>(IOrderedQueryable<T> source)
    {
        public IOrderedQueryable<T> thenBy<TKey>(Expression<Func<T, TKey>> key) => source.ThenBy(key);
        public IOrderedQueryable<T> thenByDescending<TKey>(Expression<Func<T, TKey>> key) => source.ThenByDescending(key);
    }
}
