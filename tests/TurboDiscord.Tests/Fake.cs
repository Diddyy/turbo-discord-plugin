using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace TurboDiscord.Tests;

/// <summary>
/// A host interface the test answers by method name; an unanswered call returns a completed task
/// or default. For the host's wide services (moderation, sanctions, availability).
/// </summary>
public class Fake<T> : DispatchProxy
    where T : class
{
    public Dictionary<string, Func<object?[]?, object?>> Answers { get; } = [];

    public static (T Service, Fake<T> Fake) Create()
    {
        var service = DispatchProxy.Create<T, Fake<T>>();

        return (service, (Fake<T>)(object)service);
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (Answers.TryGetValue(method!.Name, out var answer))
            return answer(args);

        var type = method.ReturnType;

        if (type == typeof(Task))
            return Task.CompletedTask;

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            return typeof(Task)
                .GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(type.GetGenericArguments()[0])
                .Invoke(null, [Default(type.GetGenericArguments()[0])]);

        return Default(type);
    }

    private static object? Default(Type type) =>
        type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
}
