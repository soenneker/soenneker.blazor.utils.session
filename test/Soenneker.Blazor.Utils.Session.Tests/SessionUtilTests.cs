using Soenneker.Tests.HostedUnit;

namespace Soenneker.Blazor.Utils.Session.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public class SessionUtilTests : HostedUnitTest
{
    public SessionUtilTests(Host host) : base(host)
    {
    }

    [Test]
    public void Default()
    {

    }
}
