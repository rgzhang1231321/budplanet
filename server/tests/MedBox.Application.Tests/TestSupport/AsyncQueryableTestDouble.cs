using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace MedBox.Application.Tests.TestSupport;

/// <summary>
/// 让内存 <see cref="IQueryable{T}"/> 支持 EF 的 <c>*Async</c> 扩展（ToListAsync / FirstOrDefaultAsync /
/// CountAsync / SumAsync…）。<see cref="Abstractions.IAppDbContext"/> 暴露的是 IQueryable 而不是 DbSet，
/// 所以测试替身必须自己提供一个实现了 <see cref="IAsyncQueryProvider"/> 的 Provider，
/// 否则 <c>ToListAsync</c> 会在运行时抛「The IQueryable is not of type EntityQueryable」。
///
/// 四个坑（都踩过，别改回去）：
/// 1. <see cref="IAsyncQueryProvider.ExecuteAsync{TResult}"/> 收到的 TResult 是 <c>Task&lt;T&gt;</c>
///    （ToListAsync 是 <c>Task&lt;List&lt;T&gt;&gt;</c>），必须把同步执行结果包成 Task 返回，不能直接回对象。
/// 2. <see cref="IAsyncQueryProvider"/> 在 <c>Microsoft.EntityFrameworkCore.Query</c> 命名空间，
///    不在 <c>Microsoft.EntityFrameworkCore</c>。
/// 3. EF 的 <c>AsNoTracking()</c> 会把表达式再包一层它自己的扩展方法调用，识别方式是
///    <c>Method.DeclaringType == typeof(EntityFrameworkQueryableExtensions)</c>。内存替身没有"跟踪"概念，
///    这些节点必须剥掉，否则求值时没人认识它。
/// 4. <c>CreateQuery</c> 收到的是**中间**表达式（Where/Select 的产物），而 EnumerableQuery 的 Provider
///    只会求值**终结**表达式（ToList/FirstOrDefault/Count）。直接把中间表达式丢给它会抛
///    "Expression of type IEnumerable&lt;T&gt; cannot be used for return type IQueryable&lt;T&gt;"。
///    解法是套一层 <see cref="Enumerable.ToList{T}"/> 把它变成终结表达式。
/// 5. 必须同时实现 <see cref="IOrderedQueryable{T}"/>（它只是标记接口，没有额外成员）：
///    <c>Queryable.OrderBy</c> / <c>ThenBy</c> 会把 <c>Provider.CreateQuery</c> 的返回值直接强转成
///    <c>IOrderedQueryable&lt;T&gt;</c>，不实现就在 `OrderBy(...)` 那一行抛 InvalidCastException ——
///    而排序在这里已经由求值结果（List）保序，不需要额外做什么。
/// </summary>
internal sealed class TestAsyncQueryable<T> : IQueryable<T>, IOrderedQueryable<T>, IAsyncEnumerable<T>
{
    private readonly IQueryable<T> _inner;

    public TestAsyncQueryable(IEnumerable<T> source)
        : this(source.AsQueryable())
    {
    }

    private TestAsyncQueryable(IQueryable<T> inner) => _inner = inner;

    public Type ElementType => _inner.ElementType;

    public Expression Expression => _inner.Expression;

    public IQueryProvider Provider => new TestAsyncQueryProvider(_inner.Provider);

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => _inner.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _inner.GetEnumerator();

    public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken ct = default)
    {
        foreach (var item in _inner)
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    private sealed class TestAsyncQueryProvider(IQueryProvider inner) : IAsyncQueryProvider
    {
        // ToList<T>(IEnumerable<T>) 与 FromResult<T>(T) 都只有一个同名方法，直接按名字取
        private static readonly MethodInfo EnumerableToList =
            typeof(Enumerable).GetMethod(nameof(Enumerable.ToList), BindingFlags.Public | BindingFlags.Static)!;

        private static readonly MethodInfo TaskFromResult =
            typeof(Task).GetMethod(nameof(Task.FromResult), BindingFlags.Public | BindingFlags.Static)!;

        public IQueryable CreateQuery(Expression expression) => CreateQuery<object>(expression);

        public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
        {
            // 关键一步：Where/Select 的产物是"中间表达式"，必须补一个终结调用才能被求值
            var terminal = Expression.Call(null, EnumerableToList.MakeGenericMethod(typeof(TElement)),
                StripEfWrappers(expression));
            var rows = (IEnumerable<TElement>?)inner.Execute(terminal);
            return new TestAsyncQueryable<TElement>(rows ?? []);
        }

        public object? Execute(Expression expression) => inner.Execute(StripEfWrappers(expression));

        public TResult Execute<TResult>(Expression expression) => inner.Execute<TResult>(StripEfWrappers(expression));

        /// <summary>EF 的 *Async 扩展最终都落到这里，TResult 形如 Task&lt;List&lt;T&gt; / Task&lt;T&gt; / Task&lt;int&gt;。</summary>
        public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
        {
            var value = inner.Execute(StripEfWrappers(expression));
            var taskType = typeof(TResult);
            if (taskType.IsGenericType && taskType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var itemType = taskType.GetGenericArguments()[0];
                var fromResult = TaskFromResult.MakeGenericMethod(itemType);
                return (TResult)fromResult.Invoke(null, [value])!;
            }

            // 不是 Task<T>（例如 IAsyncEnumerable<T>）：原样返回同步求值结果
            return (TResult)value!;
        }

        /// <summary>
        /// 剥掉 EF 查询扩展（AsNoTracking / AsTracking / IgnoreQueryFilters / AsSplitQuery / TagWith…）的包装。
        /// 内存替身没有"跟踪"概念，这些节点对结果没有影响，但 EnumerableQuery 不认识它们。
        /// </summary>
        private static Expression StripEfWrappers(Expression expression)
        {
            while (expression is MethodCallExpression call
                   && call.Method.DeclaringType == typeof(EntityFrameworkQueryableExtensions)
                   && call.Arguments.Count == 1)
            {
                expression = call.Arguments[0];
            }

            return expression;
        }
    }
}

/// <summary>把内存集合变成可被 <see cref="Abstractions.IAppDbContext"/> 替身返回的"异步可查询"集合。</summary>
internal static class AsyncQueryableExtensions
{
    public static IQueryable<T> AsAsyncQueryable<T>(this IEnumerable<T> source) => new TestAsyncQueryable<T>(source);
}
