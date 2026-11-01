using JellyShare.Application.Behaviors;
using JellyShare.Application.Dispatching;
using JellyShare.Application.Setup;
using JellyShare.Domain.Interfaces.Messaging;
using JellyShare.Domain.Results;
using JellyShare.UnitTests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JellyShare.UnitTests.Dispatching;

public sealed record EchoQuery(string Text) : IQuery<string>;

public sealed record UnhandledQuery : IQuery<int>;

public sealed class EchoQueryHandler : IQueryHandler<EchoQuery, string>
{
    public Task<Result<string>> Handle(EchoQuery request, CancellationToken cancellationToken)
        => Task.FromResult(request.Text == "fail"
            ? Result.Failure<string>(Error.NotFound("Echo.NotFound", "nothing to echo"))
            : Result.Success(request.Text));
}

public sealed class EchoQueryValidator : IValidator<EchoQuery>
{
    public Error? Validate(EchoQuery request)
        => string.IsNullOrEmpty(request.Text) ? Error.Validation("Echo.Empty", "Enter some text.") : null;
}

public sealed class SenderTests
{
    private static ServiceProvider Build(Action<IServiceCollection>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<ISender, Sender>();
        services.AddSingleton<IRequestHandler<EchoQuery, Result<string>>, EchoQueryHandler>();
        extra?.Invoke(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Send_RunsTheHandler()
    {
        using var sp = Build();

        var result = await sp.GetRequiredService<ISender>().Send(new EchoQuery("hi"));

        Assert.True(result.IsSuccess);
        Assert.Equal("hi", result.Value);
    }

    [Fact]
    public async Task Send_WithoutAHandler_ThrowsAClearError()
    {
        using var sp = Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sp.GetRequiredService<ISender>().Send(new UnhandledQuery()));

        Assert.Contains(nameof(UnhandledQuery), ex.Message, StringComparison.Ordinal);
        Assert.Contains("AddDispatcherSetup", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_RunsBehaviorsInRegistrationOrder_OutermostFirst()
    {
        var calls = new List<string>();
        using var sp = Build(s =>
        {
            s.AddSingleton<IPipelineBehavior<EchoQuery, Result<string>>>(new RecordingBehavior("outer", calls));
            s.AddSingleton<IPipelineBehavior<EchoQuery, Result<string>>>(new RecordingBehavior("inner", calls));
        });

        await sp.GetRequiredService<ISender>().Send(new EchoQuery("hi"));

        Assert.Equal(["outer>", "inner>", "<inner", "<outer"], calls);
    }

    [Fact]
    public async Task Send_ThrowsOnNullRequest()
    {
        using var sp = Build();

        await Assert.ThrowsAsync<ArgumentNullException>(() => sp.GetRequiredService<ISender>().Send<Result<string>>(null!));
    }

    [Fact]
    public async Task DispatcherSetup_RegistersHandlersValidatorsAndBehaviorsOfTheApplicationAssembly()
    {
        var services = new ServiceCollection();
        services.AddDispatcherSetup();

        Assert.Contains(services, d => d.ServiceType == typeof(ISender) && d.Lifetime == ServiceLifetime.Singleton);
        var behaviors = services.Where(d => d.ServiceType == typeof(IPipelineBehavior<,>)).Select(d => d.ImplementationType).ToList();
        Assert.Equal([typeof(LoggingBehavior<,>), typeof(ValidationBehavior<,>)], behaviors);
        Assert.Contains(services, d => d.ServiceType == typeof(IRequestHandler<JellyShare.Domain.Models.Requests.Shares.CreateShareCommand, Result<JellyShare.Domain.Models.Admin.CreatedShareResponse>>));
        Assert.Contains(services, d => d.ServiceType == typeof(IValidator<JellyShare.Domain.Models.Requests.Shares.CreateShareCommand>));
        Assert.All(services, d => Assert.Equal(ServiceLifetime.Singleton, d.Lifetime));
        Assert.DoesNotContain(services, d => d.ImplementationType == typeof(EchoQueryHandler));
        await Task.CompletedTask;
    }

    private sealed class RecordingBehavior(string name, List<string> calls) : IPipelineBehavior<EchoQuery, Result<string>>
    {
        public async Task<Result<string>> Handle(EchoQuery request, RequestHandlerDelegate<Result<string>> next, CancellationToken cancellationToken)
        {
            calls.Add(name + ">");
            var result = await next(cancellationToken);
            calls.Add("<" + name);
            return result;
        }
    }
}

public sealed class ValidationBehaviorTests
{
    [Fact]
    public async Task InvalidRequest_ReturnsTheValidatorError_WithoutCallingTheHandler()
    {
        var behavior = new ValidationBehavior<EchoQuery, Result<string>>([new EchoQueryValidator()]);
        var called = false;

        var result = await behavior.Handle(new EchoQuery(string.Empty), _ => { called = true; return Task.FromResult(Result.Success("x")); }, CancellationToken.None);

        Assert.False(called);
        Assert.True(result.IsFailure);
        Assert.Equal("Echo.Empty", result.Error.Code);
        Assert.Equal("Enter some text.", result.Error.Description);
    }

    [Fact]
    public async Task ValidRequest_CallsTheHandler()
    {
        var behavior = new ValidationBehavior<EchoQuery, Result<string>>([new EchoQueryValidator()]);

        var result = await behavior.Handle(new EchoQuery("ok"), _ => Task.FromResult(Result.Success("handled")), CancellationToken.None);

        Assert.Equal("handled", result.Value);
    }

    [Fact]
    public async Task FirstFailingValidator_Wins()
    {
        IValidator<EchoQuery>[] validators = [new FixedValidator("First"), new FixedValidator("Second")];
        var behavior = new ValidationBehavior<EchoQuery, Result<string>>(validators);

        var result = await behavior.Handle(new EchoQuery("x"), _ => Task.FromResult(Result.Success("x")), CancellationToken.None);

        Assert.Equal("First", result.Error.Code);
    }

    [Fact]
    public async Task CommandWithoutValue_FailsAsPlainResult()
    {
        var behavior = new ValidationBehavior<NoValueCommand, Result>([new NoValueValidator()]);

        var result = await behavior.Handle(new NoValueCommand(), _ => Task.FromResult(Result.Success()), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("NoValue.Invalid", result.Error.Code);
    }

    public sealed record NoValueCommand : ICommand;

    private sealed class NoValueValidator : IValidator<NoValueCommand>
    {
        public Error? Validate(NoValueCommand request) => Error.Validation("NoValue.Invalid", "invalid");
    }

    private sealed class FixedValidator(string code) : IValidator<EchoQuery>
    {
        public Error? Validate(EchoQuery request) => Error.Validation(code, code);
    }
}

public sealed class LoggingBehaviorTests
{
    [Fact]
    public async Task Success_IsLoggedAtDebug_ByTypeNameOnly()
    {
        var logger = new LevelLogger<LoggingBehavior<EchoQuery, Result<string>>>();
        var behavior = new LoggingBehavior<EchoQuery, Result<string>>(logger);

        await behavior.Handle(new EchoQuery("secret-token-value"), _ => Task.FromResult(Result.Success("x")), CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Contains(nameof(EchoQuery), entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token-value", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failure_IsLoggedWithItsCode()
    {
        var logger = new LevelLogger<LoggingBehavior<EchoQuery, Result<string>>>();
        var behavior = new LoggingBehavior<EchoQuery, Result<string>>(logger);

        await behavior.Handle(new EchoQuery("x"), _ => Task.FromResult(Result.Failure<string>(Error.NotFound("Echo.NotFound", "n"))), CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Contains("Echo.NotFound", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exception_IsLoggedAsError_AndRethrown()
    {
        var logger = new LevelLogger<LoggingBehavior<EchoQuery, Result<string>>>();
        var behavior = new LoggingBehavior<EchoQuery, Result<string>>(logger);

        await Assert.ThrowsAsync<IOException>(() => behavior.Handle(new EchoQuery("x"), _ => throw new IOException("disk"), CancellationToken.None));

        Assert.Equal(LogLevel.Error, Assert.Single(logger.Entries).Level);
    }

    [Fact]
    public async Task Cancellation_IsRethrown_AndNotLoggedAsError()
    {
        var logger = new LevelLogger<LoggingBehavior<EchoQuery, Result<string>>>();
        var behavior = new LoggingBehavior<EchoQuery, Result<string>>(logger);

        await Assert.ThrowsAsync<OperationCanceledException>(() => behavior.Handle(new EchoQuery("x"), _ => throw new OperationCanceledException(), CancellationToken.None));

        Assert.Equal(LogLevel.Debug, Assert.Single(logger.Entries).Level);
    }
}
