using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Hestia.Modules.Alerts.Contracts;
using Hestia.Modules.Sample.Contracts;
using Hestia.Modules.Sample.Domain;
using Hestia.Modules.Sample.Infrastructure;
using Hestia.Platform.Events;
using Hestia.Platform.Persistence;
using Hestia.Testing;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime.Handlers;

namespace Hestia.Api.Tests;

/// <summary>
/// Every consumer method in the modules uses an inbox, or says that it changes no data (T52). A retry or a second
/// delivery runs all consumers of an event again, so a consumer that changes data without an inbox changes it twice.
/// </summary>
/// <remarks>
/// The check reads the compiled code (IL) of each consumer method that Wolverine found in the host, with the code that the
/// method calls in its own class (helpers, lambdas, async state machines). It accepts:
/// <list type="bullet">
/// <item>a call to <see cref="ModuleDbContext.TryReceiveAsync"/> (the inbox of the consumer's module);</item>
/// <item>a new <see cref="AlertTrigger"/> (the Alerts module writes the inbox row with the alerts: <c>IAlertChecks</c>,
/// and the Alerts module's own <c>AlertsRunner.RaiseAsync</c>);</item>
/// <item>the attribute <see cref="ChangesNoDataAttribute"/> on the method.</item>
/// </list>
/// A test that delivers each event twice needs real data for each event and each consumer, and passes when a consumer
/// finds nothing to change. This check needs no data and covers a new consumer at once.
/// </remarks>
[Collection(StartedHostCollection.Name)]
public class ConsumerInboxTests(StartedHost host)
{
    [Fact]
    public void EveryModuleConsumer_UsesAnInbox_OrChangesNoData()
    {
        var handlers = host.Services.GetRequiredService<HandlerGraph>();
        var methods = handlers.AllChains()
            .SelectMany(chain => chain.Handlers)
            .Select(call => call.Method)
            .Where(method => IsModuleCode(method.DeclaringType!))
            .Distinct()
            .ToList();

        // The check sees the consumers of all modules (a guard against an empty list).
        var types = methods.Select(method => method.DeclaringType!.Name).ToHashSet();
        Assert.Contains("SampleItemCreatedConsumer", types);
        Assert.Contains("FinanceAlertsConsumer", types);
        Assert.Contains("DocumentAlertsConsumer", types);

        var problems = methods.Select(InboxCheck.Problem).OfType<string>().ToList();
        if (problems.Count > 0)
        {
            Assert.Fail(string.Join(Environment.NewLine, problems));
        }
    }

    [Fact]
    public void TheCheck_AcceptsTheInbox_AnAlertTrigger_AndTheAttribute()
    {
        Assert.Null(InboxCheck.Problem(Method<InboxConsumer>()));
        Assert.Null(InboxCheck.Problem(Method<HelperInboxConsumer>()));
        Assert.Null(InboxCheck.Problem(Method<AlertConsumer>()));
        Assert.Null(InboxCheck.Problem(Method<MarkedConsumer>()));
    }

    [Fact]
    public void TheCheck_RefusesAConsumerWithoutAnInbox_AndNamesIt()
    {
        var problem = InboxCheck.Problem(Method<NoInboxConsumer>());

        Assert.NotNull(problem);
        Assert.Contains($"{typeof(NoInboxConsumer).FullName}.Consume({nameof(SampleItemCreated)})", problem);
        Assert.Contains("TryReceiveAsync", problem);
        Assert.Contains(nameof(ChangesNoDataAttribute), problem);
    }

    private static bool IsModuleCode(Type type)
    {
        var assembly = type.Assembly.GetName().Name ?? "";
        return assembly.StartsWith("Hestia.Modules.", StringComparison.Ordinal) &&
               !assembly.EndsWith(".Tests", StringComparison.Ordinal);
    }

    private static MethodInfo Method<T>() => typeof(T).GetMethod("Consume")!;

    // Test consumers for the check itself. Wolverine does not see them.

    public sealed class InboxConsumer(SampleDbContext db)
    {
        public async Task Consume(SampleItemCreated message, CancellationToken cancellationToken)
        {
            if (!await db.TryReceiveAsync(message.EventId, "Tests.InboxConsumer", cancellationToken))
            {
                return;
            }
            db.Activity.Add(new SampleActivityEntry { Id = Guid.CreateVersion7(), ItemId = message.ItemId, Name = message.Name });
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public sealed class HelperInboxConsumer(SampleDbContext db)
    {
        public Task Consume(SampleItemCreated message) => SaveAsync(message.EventId);

        private async Task SaveAsync(Guid eventId)
        {
            if (await db.TryReceiveAsync(eventId, "Tests.HelperInboxConsumer"))
            {
                await db.SaveChangesAsync();
            }
        }
    }

    public sealed class AlertConsumer(IAlertChecks alerts)
    {
        public Task Consume(SampleItemCreated message) =>
            alerts.CheckAsync("source", message.ItemId.ToString(), new AlertTrigger(message.EventId, "Tests.AlertConsumer"));
    }

    public sealed class MarkedConsumer(IAlertChecks alerts)
    {
        [ChangesNoData("It only asks for a check.")]
        public void Consume(SampleItemCreated message) => alerts.RequestCheck("source");
    }

    public sealed class NoInboxConsumer(SampleDbContext db)
    {
        public async Task Consume(SampleItemCreated message)
        {
            db.Activity.Add(new SampleActivityEntry { Id = Guid.CreateVersion7(), ItemId = message.ItemId, Name = message.Name });
            await db.SaveChangesAsync();
        }
    }
}

/// <summary>Reads the IL of a consumer method for the inbox (see <see cref="ConsumerInboxTests"/>).</summary>
internal static class InboxCheck
{
    private static readonly Dictionary<ushort, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => (ushort)code.Value);

    /// <summary>Null when the method uses an inbox or has the attribute; else a message that names it and the fix.</summary>
    public static string? Problem(MethodInfo method)
    {
        if (method.GetCustomAttribute<ChangesNoDataAttribute>() is not null || UsesInbox(method))
        {
            return null;
        }
        var parameters = string.Join(", ", method.GetParameters()
            .Where(p => p.ParameterType != typeof(CancellationToken))
            .Select(p => p.ParameterType.Name));
        return $"{method.DeclaringType!.FullName}.{method.Name}({parameters}) uses no inbox. A consumer that changes data " +
               $"calls ModuleDbContext.TryReceiveAsync first (or gives an AlertTrigger to the Alerts module). A method that " +
               $"changes no data has [{nameof(ChangesNoDataAttribute)}(\"why\")]. See docs/new-module.md, step 6.";
    }

    private static bool UsesInbox(MethodInfo consumer)
    {
        var owner = consumer.DeclaringType!;
        var seen = new HashSet<MethodBase>();
        var queue = new Queue<MethodBase>([consumer]);
        while (queue.TryDequeue(out var method))
        {
            if (!seen.Add(method))
            {
                continue;
            }
            // An async method keeps its code in a state machine class.
            if (method.GetCustomAttribute<AsyncStateMachineAttribute>() is { } machine &&
                machine.StateMachineType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is { } moveNext)
            {
                queue.Enqueue(moveNext);
            }
            foreach (var called in CalledMethods(method))
            {
                if (IsInbox(called))
                {
                    return true;
                }
                // Follow the code of the consumer's own class: helpers, lambdas, and local functions.
                if (called.DeclaringType is { } type && IsInside(type, owner))
                {
                    queue.Enqueue(called);
                }
            }
        }
        return false;
    }

    private static bool IsInbox(MethodBase method) =>
        (method.DeclaringType == typeof(ModuleDbContext) && method.Name == nameof(ModuleDbContext.TryReceiveAsync)) ||
        (method is ConstructorInfo && method.DeclaringType == typeof(AlertTrigger));

    private static bool IsInside(Type type, Type owner)
    {
        for (var current = type; current is not null; current = current.DeclaringType)
        {
            if (current == owner || (current.IsGenericType && current.GetGenericTypeDefinition() == owner))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The methods and constructors that the IL of <paramref name="method"/> calls or refers to.</summary>
    private static List<MethodBase> CalledMethods(MethodBase method)
    {
        var found = new List<MethodBase>();
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            return found;
        }
        var typeArguments = method.DeclaringType is { IsGenericType: true } type ? type.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var position = 0;
        while (position < il.Length)
        {
            OpCode code;
            if (il[position] == 0xFE)
            {
                code = OpCodesByValue[(ushort)(0xFE00 | il[position + 1])];
                position += 2;
            }
            else
            {
                code = OpCodesByValue[il[position]];
                position += 1;
            }

            switch (code.OperandType)
            {
                case OperandType.InlineMethod:
                    try
                    {
                        if (method.Module.ResolveMethod(BitConverter.ToInt32(il, position), typeArguments, methodArguments) is { } target)
                        {
                            found.Add(target);
                        }
                    }
                    catch (Exception exception) when (exception is ArgumentException or BadImageFormatException)
                    {
                        // A method that cannot be resolved here (for example in a generic context) is no inbox.
                    }
                    position += 4;
                    break;
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar:
                    position += 1;
                    break;
                case OperandType.InlineVar:
                    position += 2;
                    break;
                case OperandType.InlineI8 or OperandType.InlineR:
                    position += 8;
                    break;
                case OperandType.InlineSwitch:
                    position += 4 + 4 * BitConverter.ToInt32(il, position);
                    break;
                default:
                    position += 4;
                    break;
            }
        }
        return found;
    }
}
