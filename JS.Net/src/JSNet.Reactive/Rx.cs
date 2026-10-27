using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace JSNet.Reactive;

/// <summary>
/// RxJS-style creation functions: <c>rx.of(1, 2, 3)</c>, <c>rx.interval(1000)</c>, <c>rx.subject&lt;string&gt;()</c>.
/// Observables are standard <see cref="IObservable{T}"/>, so they interoperate with System.Reactive, Blazor, WPF and Avalonia.
/// </summary>
public static class rx
{
    public static IObservable<T> of<T>(params T[] values) => Observable.ToObservable<T>(values);
    public static IObservable<T> from<T>(IEnumerable<T> values) => Observable.ToObservable<T>(values);
    public static IObservable<T> from<T>(Task<T> task) => System.Reactive.Threading.Tasks.TaskObservableExtensions.ToObservable(task);
    public static IObservable<long> interval(double ms) => Observable.Interval(TimeSpan.FromMilliseconds(ms));
    public static IObservable<long> timer(double ms) => Observable.Timer(TimeSpan.FromMilliseconds(ms));
    public static IObservable<T> empty<T>() => Observable.Empty<T>();
    public static IObservable<T> merge<T>(params IObservable<T>[] sources) => sources.Merge();
    public static IObservable<IList<T>> combineLatest<T>(params IObservable<T>[] sources) => sources.CombineLatest();
    public static Subject<T> subject<T>() => new();
    public static BehaviorSubject<T> behaviorSubject<T>(T initial) => new(initial);
    public static ReplaySubject<T> replaySubject<T>(int bufferSize = int.MaxValue) => new(bufferSize);
}

/// <summary>RxJS operator names on <see cref="IObservable{T}"/> (C# 14 extension members).</summary>
public static class JsObservableExtensions
{
    extension<T>(IObservable<T> source)
    {
        public IObservable<TResult> map<TResult>(Func<T, TResult> selector) => source.Select(selector);
        public IObservable<T> filter(Func<T, bool> predicate) => source.Where(predicate);
        public IObservable<T> take(int count) => source.Take(count);
        public IObservable<T> skip(int count) => source.Skip(count);
        public IObservable<T> tap(Action<T> action) => source.Do(action);
        public IObservable<TAcc> scan<TAcc>(TAcc seed, Func<TAcc, T, TAcc> accumulator) => source.Scan(seed, accumulator);
        public IObservable<T> debounceTime(double ms) => source.Throttle(TimeSpan.FromMilliseconds(ms));
        public IObservable<T> throttleTime(double ms) => source.Sample(TimeSpan.FromMilliseconds(ms));
        public IObservable<T> delay(double ms) => source.Delay(TimeSpan.FromMilliseconds(ms));
        public IObservable<T> distinctUntilChanged() => source.DistinctUntilChanged();
        public IObservable<T> startWith(params T[] values) => source.StartWith(values);
        public IObservable<T> mergeWith(IObservable<T> other) => source.Merge(other);
        public IObservable<TResult> switchMap<TResult>(Func<T, IObservable<TResult>> selector) => source.Select(selector).Switch();
        public IObservable<TResult> mergeMap<TResult>(Func<T, IObservable<TResult>> selector) => source.SelectMany(selector);
        public IObservable<IList<T>> bufferCount(int count) => source.Buffer(count);
        public IObservable<T> catchError(Func<Exception, IObservable<T>> handler) => source.Catch(handler);

        public IDisposable subscribe(Action<T> next) => source.Subscribe(next);
        public IDisposable subscribe(Action<T> next, Action<Exception> error) => source.Subscribe(next, error);
        public IDisposable subscribe(Action<T> next, Action<Exception> error, Action complete) => source.Subscribe(next, error, complete);

        /// <summary>Collects every value until completion: <c>await rx.of(1, 2).toArrayAsync()</c>.</summary>
        public async Task<List<T>> toArrayAsync() => [.. await source.ToList()];
        /// <summary>Last value (RxJS <c>lastValueFrom</c>).</summary>
        public async Task<T> lastValue() => await source.LastAsync();
        /// <summary>First value (RxJS <c>firstValueFrom</c>).</summary>
        public async Task<T> firstValue() => await source.FirstAsync();
    }

    extension<T>(ISubject<T> subject)
    {
        public void next(T value) => subject.OnNext(value);
        public void complete() => subject.OnCompleted();
        public void error(Exception error) => subject.OnError(error);
    }
}
