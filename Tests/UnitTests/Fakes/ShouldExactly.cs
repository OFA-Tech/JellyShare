namespace JellyShare.UnitTests.Fakes;

/// <summary>Like <c>Should.Throw</c>, but the exception must be exactly <typeparamref name="TException"/>, not a subclass.</summary>
internal static class ShouldExactly
{
    public static TException Throw<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            return ex.ShouldBeOfType<TException>();
        }

        throw new ShouldAssertException($"Expected {typeof(TException).Name} but nothing was thrown.");
    }

    public static TException Throw<TException>(Func<object?> func)
        where TException : Exception
        => Throw<TException>(new Action(() => _ = func()));

    public static async Task<TException> ThrowAsync<TException>(Func<Task> func)
        where TException : Exception
    {
        try
        {
            await func();
        }
        catch (Exception ex)
        {
            return ex.ShouldBeOfType<TException>();
        }

        throw new ShouldAssertException($"Expected {typeof(TException).Name} but nothing was thrown.");
    }
}
