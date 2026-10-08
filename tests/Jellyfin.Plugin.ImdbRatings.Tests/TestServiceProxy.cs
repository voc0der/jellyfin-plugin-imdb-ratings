using System.Reflection;

namespace Jellyfin.Plugin.ImdbRatings.Tests;

public class TestServiceProxy : DispatchProxy
{
    private Func<MethodInfo, object?[]?, object?> _invoke = null!;

    public static T Create<T>(Func<MethodInfo, object?[]?, object?> invoke) where T : class
    {
        var proxy = Create<T, TestServiceProxy>();
        ((TestServiceProxy)(object)proxy)._invoke = invoke;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => _invoke(targetMethod!, args);
}
