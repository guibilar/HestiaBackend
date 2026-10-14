namespace Hestia.Api.Tests;

/// <summary>
/// The tests that listen to the host queue <c>hestia.api.finance-events</c> on the test broker. Each test host is a
/// consumer of the same queue, so two tests at the same time take each other's events. The tests of this collection
/// run one after the other (T37).
/// </summary>
[CollectionDefinition(Name)]
public sealed class BrokerCollection
{
    public const string Name = "Finance events broker";
}
